using System.IO;
using System.Xml.Linq;

namespace WilcoATC.Atc.Planning;

/// <summary>
/// Importe un plan de vol dans le <see cref="FlightPlanStore"/> :
///  - depuis l'API SimBrief (username) — voie principale ;
///  - depuis un fichier OFP XML exporté — voie secondaire (optionnelle).
/// Retourne un message de statut lisible (jamais d'exception vers l'UI).
/// </summary>
public sealed class FlightPlanImporter
{
    private readonly ISimBriefClient _simBrief;
    private readonly FlightPlanStore _store;

    public FlightPlanImporter(ISimBriefClient simBrief, FlightPlanStore store)
    {
        _simBrief = simBrief;
        _store = store;
    }

    public async Task<string> ImportFromSimBriefAsync(string? username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username))
            return "Renseignez d'abord votre nom d'utilisateur SimBrief.";
        try
        {
            var plan = await _simBrief.FetchAsync(username!, ct);
            _store.Set(plan);
            Trace(plan);
            return "Plan importé : " + plan.Summary + "\n" + plan.DebugSummary;
        }
        catch (SimBriefException ex) { return "SimBrief : " + ex.Message; }
        catch (Exception ex) { return "Échec de l'import : " + ex.Message; }
    }

    public string ImportFromOfpFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return "Fichier introuvable.";
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null) return "Fichier OFP invalide.";

            var plan = ParseOfpXml(root);
            _store.Set(plan);
            Trace(plan);
            return "Plan importé (fichier) : " + plan.Summary + "\n" + plan.DebugSummary;
        }
        catch (Exception ex) { return "Échec de lecture du fichier : " + ex.Message; }
    }

    /// <summary>Trace de diagnostic : quelle procédure/piste est rattachée au départ, laquelle à l'arrivée.</summary>
    private static void Trace(FlightPlan p)
        => System.Diagnostics.Debug.WriteLine("[WilcoATC/Plan] " + p.DebugSummary);

    // L'OFP XML SimBrief a les mêmes noms d'éléments que le JSON.
    private static FlightPlan ParseOfpXml(XElement root)
    {
        XElement? g = root.Element("general");
        XElement? o = root.Element("origin");
        XElement? d = root.Element("destination");
        XElement? a = root.Element("alternate");
        XElement? ac = root.Element("aircraft");
        XElement? atc = root.Element("atc");

        ExtractSidStar(root, out string? sid, out string? star);

        return new FlightPlan
        {
            OriginIcao = V(o, "icao_code"),
            OriginName = V(o, "name"),
            OriginRunway = V(o, "plan_rwy"),
            DestinationIcao = V(d, "icao_code"),
            DestinationName = V(d, "name"),
            DestinationRunway = V(d, "plan_rwy"),
            AlternateIcao = V(a, "icao_code"),
            Route = V(g, "route"),
            CruiseAltitudeFeet = int.TryParse(V(g, "initial_altitude"), out var ft) ? ft : 0,
            AirlineIcao = V(g, "icao_airline"),
            FlightNumber = V(g, "flight_number"),
            AtcCallsign = V(atc, "callsign"),
            AircraftIcao = V(ac, "icaocode") ?? V(ac, "icao_code"),
            SidName = sid,
            StarName = star,
        };
    }

    /// <summary>
    /// Mêmes règles que pour le JSON : le drapeau « 2 » (STAR) ne peut jamais devenir le SID,
    /// et le SID doit se trouver en tête de route.
    /// </summary>
    private static void ExtractSidStar(XElement root, out string? sid, out string? star)
    {
        sid = null; star = null;
        var navlog = root.Element("navlog");
        if (navlog is null) return;

        bool seenEnroute = false;
        foreach (var f in navlog.Elements("fix"))
        {
            string flag = (f.Element("is_sid_star")?.Value ?? "0").Trim();
            bool isSid = flag == "1";
            bool isStar = flag == "2";

            if (!isSid && !isStar) { seenEnroute = true; continue; }

            string via = (f.Element("via_airway")?.Value ?? "").Trim();
            if (via.Length == 0
                || via.Equals("DCT", StringComparison.OrdinalIgnoreCase)
                || via.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)) continue;

            if (isSid && !seenEnroute) sid ??= via;
            else if (isStar) star ??= via;
        }
    }

    private static string? V(XElement? parent, string child) => parent?.Element(child)?.Value;
}
