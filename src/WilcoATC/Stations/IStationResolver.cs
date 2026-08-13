using WilcoATC.Common;

namespace WilcoATC.Stations;

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

    /// <summary>
    /// TOUTES les fréquences publiées d'un aéroport, triées dans l'ordre d'utilisation d'un
    /// vol (ATIS → Délivrance → Sol → Tour → Départ → Approche → Centre). Liste vide si
    /// l'aéroport est inconnu.
    /// </summary>
    IReadOnlyList<AirportFrequency> ListFrequencies(string icao);

    /// <summary>Fréquence (Hz) d'un type de contrôleur donné pour un aéroport ICAO, ou null.</summary>
    double? FindFrequencyHz(string icao, ControllerType controller);

    /// <summary>
    /// Fréquence (Hz) la PLUS PROCHE géographiquement pour un type de contrôleur, dans un
    /// rayon donné. Indispensable pour le Centre en-route : c'est un service de SECTEUR, que
    /// les données rattachent aux petits terrains survolés et non aux grands aéroports.
    /// </summary>
    double? FindNearestFrequencyHz(ControllerType controller, double lat, double lon, double maxKm);

    /// <summary>
    /// ICAO de l'aéroport CONTRÔLÉ (avec une fréquence Tour) le plus proche, ou null.
    /// <paramref name="includeSmallFields"/> ouvre la recherche aux petits terrains : ce
    /// qui n'a aucun sens en IFR est justement la destination normale d'un vol à vue.
    /// </summary>
    string? NearestControlledAirportIcao(double lat, double lon, bool includeSmallFields = false);

    /// <summary>
    /// Position d'un aéroport (degrés), ou null. Sert à mesurer la distance à l'arrivée
    /// quand aucun plan de vol n'est chargé.
    /// </summary>
    (double Lat, double Lon)? AirportPosition(string icao);

    /// <summary>
    /// Aéroport « opérationnel » à retenir (affichage des fréquences + calage ATC) à partir
    /// du plus proche signalé par le simulateur. Si ce dernier ne publie AUCUNE fréquence —
    /// typiquement une base militaire mitoyenne d'un aéroport civil, comme EBMB (Melsbroek)
    /// à côté d'EBBR (Bruxelles) — on lui substitue un terrain à fréquences CO-LOCALISÉ (à
    /// quelques km). Sinon on le garde tel quel. Renvoie l'entrée inchangée si les données
    /// ne sont pas chargées.
    /// </summary>
    string? OperationalAirport(string? nearestIcao, double lat, double lon);

    /// <summary>
    /// Levé (sur un thread de fond) quand des fréquences LIVE issues du simulateur viennent
    /// d'arriver pour un ICAO — l'UI peut alors rafraîchir le panneau. Jamais levé par les
    /// résolveurs purement hors-ligne (CSV / inerte).
    /// </summary>
    event Action<string>? FrequenciesUpdated;

    /// <summary>
    /// Importe un fichier CSV de fréquences (colonnes <c>icao,type,mhz</c>) — corrections/ajouts
    /// communautaires validés — dans l'overlay UTILISATEUR (fusionné, dédoublonné) puis recharge.
    /// Renvoie le nombre de lignes NOUVELLES effectivement ajoutées. 0 si non pris en charge.
    /// </summary>
    int ImportOverlay(string csvPath);
}
