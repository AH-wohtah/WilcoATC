using FreqWatch.Common;

namespace FreqWatch.Stations;

/// <summary>
/// (Stretch, isolé) Données aéronautiques OurAirports, optionnelles.
/// Sans les fichiers CSV, toutes les méthodes renvoient null.
/// </summary>
public interface IStationResolver
{
    /// <summary>Associe une fréquence active à une station à proximité (ex. "Paris CDG · TWR").</summary>
    string? Resolve(double activeHz, double lat, double lon);

    /// <summary>Station structurée (nom + type de contrôleur) pour la validation ATC.</summary>
    StationInfo? ResolveStation(double activeHz, double lat, double lon);

    /// <summary>Nom complet d'un aéroport à partir de son code ICAO.</summary>
    string? LookupAirportName(string icao);

    /// <summary>Fréquence (Hz) d'un type de contrôleur donné pour un aéroport ICAO, ou null.</summary>
    double? FindFrequencyHz(string icao, ControllerType controller);

    /// <summary>ICAO de l'aéroport CONTRÔLÉ (avec une fréquence Tour) le plus proche, ou null.</summary>
    string? NearestControlledAirportIcao(double lat, double lon);
}
