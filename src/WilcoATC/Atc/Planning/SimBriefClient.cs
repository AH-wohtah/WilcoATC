using System.Net.Http;
using System.Text.Json;

namespace FreqWatch.Atc.Planning;

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
            throw new SimBriefException("Aucun nom d'utilisateur SimBrief renseigné.");

        string url = $"{Endpoint}?username={Uri.EscapeDataString(username.Trim())}&json=1";

        HttpResponseMessage resp;
        try { resp = await Http.GetAsync(url, ct); }
        catch (Exception ex) { throw new SimBriefException("Réseau indisponible : " + ex.Message); }

        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new SimBriefException($"SimBrief a répondu {(int)resp.StatusCode}. Vérifiez le username.");

            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch { throw new SimBriefException("Réponse SimBrief illisible (username inconnu ?)."); }

            using (doc)
            {
                var root = doc.RootElement;

                // Statut de récupération : "Success" attendu, sinon message d'erreur SimBrief.
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
            DestinationIcao = In(dest, "icao_code"),
            DestinationName = In(dest, "name"),
            DestinationLat = Dbl(dest, "pos_lat"),
            DestinationLon = Dbl(dest, "pos_long"),
            AlternateIcao = In(alt, "icao_code"),
            Route = In(general, "route"),
            CruiseAltitudeFeet = ParseFeet(In(general, "initial_altitude") ?? In(general, "cruise_altitude")),
            AirlineIcao = In(general, "icao_airline"),
            FlightNumber = In(general, "flight_number"),
            AtcCallsign = In(atc, "callsign"),
            AircraftIcao = In(aircraft, "icaocode") ?? In(aircraft, "icao_code"),
            SidName = sid,
            StarName = star,
        };
    }

    // navlog.fix[] : is_sid_star "1"=SID / "2"=STAR ; nom dans via_airway.
    private static void ExtractSidStar(JsonElement root, out string? sid, out string? star)
    {
        sid = null; star = null;
        var navlog = SimBriefClient.Obj(root, "navlog");
        if (navlog is not { } nl || !nl.TryGetProperty("fix", out var fixes)) return;

        IEnumerable<JsonElement> items = fixes.ValueKind == JsonValueKind.Array
            ? fixes.EnumerateArray()
            : new[] { fixes };

        foreach (var f in items)
        {
            string? via = SimBriefClient.Str(f, "via_airway");
            if (string.IsNullOrWhiteSpace(via)) continue;
            string? flag = SimBriefClient.Str(f, "is_sid_star");
            if (flag == "1" && sid is null) sid = via.Trim();
            else if (flag == "2" && star is null) star = via.Trim();
        }
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
