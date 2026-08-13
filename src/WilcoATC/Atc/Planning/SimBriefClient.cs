using System.Net.Http;
using System.Text.Json;

namespace WilcoATC.Atc.Planning;

/// <summary>Erreur d'import SimBrief (username inconnu, aucun plan, réseau…).</summary>
public sealed class SimBriefException : Exception
{
    public SimBriefException(string message) : base(message) { }
}

public interface ISimBriefClient
{
    Task<FlightPlan> FetchAsync(string username, CancellationToken ct = default);
}

/// <summary>
/// Récupère le dernier OFP d'un utilisateur via l'API GRATUITE et SANS CLÉ de SimBrief :
/// <c>https://www.simbrief.com/api/xml.fetcher.php?username={username}&amp;json=1</c>.
///
/// Le JSON est parsé de façon DÉFENSIVE (chemins connus + repli si champ absent) afin
/// de rester robuste aux petites variations du schéma SimBrief.
/// </summary>
public sealed class SimBriefClient : ISimBriefClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const string Endpoint = "https://www.simbrief.com/api/xml.fetcher.php";

    public async Task<FlightPlan> FetchAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new SimBriefException("No SimBrief username set.");

        string url = $"{Endpoint}?username={Uri.EscapeDataString(username.Trim())}&json=1";

        HttpResponseMessage resp;
        try { resp = await Http.GetAsync(url, ct); }
        catch (Exception ex) { throw new SimBriefException("Network unavailable: " + ex.Message); }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new SimBriefException($"SimBrief responded {(int)resp.StatusCode}. Check the username.");

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch { throw new SimBriefException("SimBrief response could not be read (unknown username?)."); }

            using (doc)
            {
                var root = doc.RootElement;

                // Statut de récupération : « success » attendu, sinon message d'erreur SimBrief.
                var fetch = Obj(root, "fetch");
                if (fetch is { } fe)
                {
                    string? status = Str(fe, "status");
                    if (!string.IsNullOrEmpty(status) &&
                        !status.Contains("success", StringComparison.OrdinalIgnoreCase))
                        throw new SimBriefException(status!);
                }

                return SimBriefParser.Parse(root);
            }
        }
    }

    internal static JsonElement? Obj(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.Object ? v : null;

    internal static string? Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
           && v.ValueKind is JsonValueKind.String or JsonValueKind.Number ? v.ToString() : null;
}

/// <summary>Extraction commune du <see cref="FlightPlan"/> depuis un JSON SimBrief.</summary>
public static class SimBriefParser
{
    public static FlightPlan Parse(JsonElement root)
    {
        var general = SimBriefClient.Obj(root, "general");
        var origin = SimBriefClient.Obj(root, "origin");
        var dest = SimBriefClient.Obj(root, "destination");
        var alt = SimBriefClient.Obj(root, "alternate");
        var aircraft = SimBriefClient.Obj(root, "aircraft");
        var atc = SimBriefClient.Obj(root, "atc");

        ExtractSidStar(root, out string? sid, out string? star);

        return new FlightPlan
        {
            OriginIcao = In(origin, "icao_code"),
            OriginName = In(origin, "name"),
            OriginLat = Dbl(origin, "pos_lat"),
            OriginLon = Dbl(origin, "pos_long"),
            OriginRunway = In(origin, "plan_rwy"),
            DestinationIcao = In(dest, "icao_code"),
            DestinationName = In(dest, "name"),
            DestinationLat = Dbl(dest, "pos_lat"),
            DestinationLon = Dbl(dest, "pos_long"),
            DestinationRunway = In(dest, "plan_rwy"),
            AlternateIcao = In(alt, "icao_code"),
            Route = In(general, "route"),
            CruiseAltitudeFeet = ParseFeet(In(general, "initial_altitude") ?? In(general, "cruise_altitude")),
            AirlineIcao = In(general, "icao_airline"),
            FlightNumber = In(general, "flight_number"),
            AtcCallsign = In(atc, "callsign"),
            AircraftIcao = In(aircraft, "icaocode") ?? In(aircraft, "icao_code"),
            SidName = sid,
            StarName = star,
            DeclaredRules = ParseRules(In(general, "flight_rules") ?? In(general, "flight_type")),
        };
    }

    /// <summary>
    /// Règles de vol déclarées par SimBrief. Le champ n'est pas garanti présent (et son nom
    /// a varié) : tout ce qui n'est pas reconnu renvoie null, et la déduction se fera sur le
    /// gabarit de l'appareil. On ne devine JAMAIS « IFR » à partir d'une chaîne inconnue.
    /// </summary>
    private static Context.FlightRules? ParseRules(string? raw)
    {
        string s = (raw ?? "").Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith("V", StringComparison.OrdinalIgnoreCase)) return Context.FlightRules.Vfr;
        if (s.StartsWith("I", StringComparison.OrdinalIgnoreCase)) return Context.FlightRules.Ifr;
        return null;
    }

    /// <summary>
    /// Extrait le SID (départ) et la STAR (arrivée) du navlog SimBrief.
    /// <c>is_sid_star</c> vaut « 1 » pour un SID, « 2 » pour une STAR ; le nom est dans
    /// <c>via_airway</c>.
    ///
    /// DEUX GARDE-FOUS, parce qu'une STAR affichée comme SID donnait une clairance de départ
    /// avec la procédure d'ARRIVÉE :
    ///  • le drapeau « 2 » ne peut JAMAIS alimenter le SID — au pire on n'a pas de SID et la
    ///    clairance repart sur « as filed » ;
    ///  • le SID doit en plus se trouver en TÊTE de route (avant le premier point en route).
    ///    Une procédure marquée « 1 » mais située en fin de navlog est donc rejetée.
    /// </summary>
    private static void ExtractSidStar(JsonElement root, out string? sid, out string? star)
    {
        sid = null; star = null;
        var navlog = SimBriefClient.Obj(root, "navlog");
        if (navlog is not { } nl || !nl.TryGetProperty("fix", out var fixes)) return;

        IEnumerable<JsonElement> items = fixes.ValueKind == JsonValueKind.Array
            ? fixes.EnumerateArray()
            : new[] { fixes };

        bool seenEnroute = false;   // a-t-on déjà dépassé la phase de départ ?

        foreach (var f in items)
        {
            string flag = SimBriefClient.Str(f, "is_sid_star")?.Trim() ?? "0";
            bool isSid = flag == "1";
            bool isStar = flag == "2";

            if (!isSid && !isStar) { seenEnroute = true; continue; }

            string? via = Procedure(SimBriefClient.Str(f, "via_airway"));
            if (via is null) continue;

            if (isSid && !seenEnroute) sid ??= via;
            else if (isStar) star ??= via;
        }
    }

    /// <summary>Nom de procédure exploitable, ou null (« DCT »/« DIRECT » n'en sont pas).</summary>
    private static string? Procedure(string? via)
    {
        string s = via?.Trim() ?? "";
        if (s.Length == 0) return null;
        return s.Equals("DCT", StringComparison.OrdinalIgnoreCase)
            || s.Equals("DIRECT", StringComparison.OrdinalIgnoreCase) ? null : s;
    }

    private static string? In(JsonElement? obj, string prop)
        => obj is { } e ? SimBriefClient.Str(e, prop) : null;

    private static int ParseFeet(string? s)
        => int.TryParse((s ?? "").Trim(), out var v) ? v : 0;

    private static double Dbl(JsonElement? obj, string prop)
        => obj is { } e && double.TryParse((SimBriefClient.Str(e, prop) ?? "").Trim(),
               System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
           ? v : 0;
}
