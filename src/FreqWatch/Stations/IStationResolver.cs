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

    /// <summary>
    /// Fréquence (Hz) la PLUS PROCHE géographiquement pour un type de contrôleur, dans un
    /// rayon donné. Indispensable pour le Centre en-route : c'est un service de SECTEUR, que
    /// les données rattachent aux petits terrains survolés et non aux grands aéroports.
    /// </summary>
    double? FindNearestFrequencyHz(ControllerType controller, double lat, double lon, double maxKm);

    /// <summary>ICAO de l'aéroport CONTRÔLÉ (avec une fréquence Tour) le plus proche, ou null.</summary>
    string? NearestControlledAirportIcao(double lat, double lon);

    /// <summary>
    /// Position d'un aéroport (degrés), ou null. Sert à mesurer la distance à l'arrivée
    /// quand aucun plan de vol n'est chargé.
    /// </summary>
    (double Lat, double Lon)? AirportPosition(string icao);
}
