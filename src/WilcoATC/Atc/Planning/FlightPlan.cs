using System.Text.RegularExpressions;

namespace FreqWatch.Atc.Planning;

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

    // SID/STAR (nom brut, ex. "SOSAL2Y") + coordonnées départ/arrivée (pour les transferts).
    public string? SidName { get; init; }
    public string? StarName { get; init; }
    public double OriginLat { get; init; }
    public double OriginLon { get; init; }
    public double DestinationLat { get; init; }
    public double DestinationLon { get; init; }

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
