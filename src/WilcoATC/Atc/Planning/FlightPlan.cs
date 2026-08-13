using System.Text.RegularExpressions;

namespace WilcoATC.Atc.Planning;

/// <summary>Plan de vol importé (SimBrief ou fichier OFP). Alimente les données ATC.</summary>
public sealed class FlightPlan
{
    public string? OriginIcao { get; init; }
    public string? OriginName { get; init; }
    public string? DestinationIcao { get; init; }
    public string? DestinationName { get; init; }
    public string? AlternateIcao { get; init; }
    public string? Route { get; init; }
    public int CruiseAltitudeFeet { get; init; }
    public string? AirlineIcao { get; init; }
    public string? FlightNumber { get; init; }
    public string? AtcCallsign { get; init; }
    public string? AircraftIcao { get; init; }

    /// <summary>
    /// Règles de vol DÉCLARÉES par le plan (« I » / « V » côté SimBrief), ou null si le
    /// champ est absent. Null ne veut pas dire IFR : c'est l'absence d'information, et la
    /// déduction repart alors sur le gabarit de l'appareil.
    /// </summary>
    public Context.FlightRules? DeclaredRules { get; init; }

    // --- DÉPART : ne doit JAMAIS être alimenté par des données d'arrivée ---
    /// <summary>Nom brut du SID (ex. « SOSAL2Y »). Départ uniquement.</summary>
    public string? SidName { get; init; }
    /// <summary>Piste planifiée AU DÉPART (ex. « 34R »). Clairance, roulage, décollage.</summary>
    public string? OriginRunway { get; init; }

    // --- ARRIVÉE : réservé à l'approche et à l'atterrissage ---
    /// <summary>Nom brut de la STAR (ex. « ELVO1L »). Arrivée uniquement — jamais en clairance de départ.</summary>
    public string? StarName { get; init; }
    /// <summary>Piste planifiée À L'ARRIVÉE. Approche / atterrissage uniquement.</summary>
    public string? DestinationRunway { get; init; }

    public double OriginLat { get; init; }
    public double OriginLon { get; init; }
    public double DestinationLat { get; init; }
    public double DestinationLon { get; init; }

    /// <summary>
    /// Trace de diagnostic : montre NOIR SUR BLANC quelle procédure et quelle piste sont
    /// rattachées au départ et lesquelles à l'arrivée, pour repérer d'un coup d'œil une
    /// donnée d'arrivée qui aurait fuité dans la clairance de départ.
    /// </summary>
    public string DebugSummary =>
        $"DÉPART  {OriginIcao ?? "?"} · SID={SidName ?? "(aucun)"} · piste={OriginRunway ?? "(inconnue)"}  ||  " +
        $"ARRIVÉE {DestinationIcao ?? "?"} · STAR={StarName ?? "(aucune)"} · piste={DestinationRunway ?? "(inconnue)"}";

    /// <summary>Nom de destination « parlable » (ville/aéroport, sans « International/Intl/Airport »).</summary>
    public string? DestinationDisplay
    {
        get
        {
            string? n = CleanAirportName(DestinationName);
            return !string.IsNullOrWhiteSpace(n) ? n : DestinationIcao;
        }
    }

    public string Summary =>
        $"{(string.IsNullOrWhiteSpace(AtcCallsign) ? $"{AirlineIcao}{FlightNumber}" : AtcCallsign)} " +
        $"{OriginIcao} → {DestinationIcao}";

    public static string? CleanAirportName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string s = Regex.Replace(name,
            @"\b(International|Intl\.?|Int'l|Airport|Regional|Airfield|Airbase|Air Base)\b",
            "", RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('/', '-').Trim();
    }
}
