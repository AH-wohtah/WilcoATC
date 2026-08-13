namespace WilcoATC.Sim;

/// <summary>
/// Contrat de la couche SimConnect. C'est la seule chose dont dépend l'UI :
/// elle ne référence jamais les types SimConnect directement.
///
/// Tous les événements sont levés depuis le thread de pompage SimConnect :
/// l'abonné (le ViewModel) est responsable du marshalling vers le thread UI.
/// </summary>
public interface ISimConnectService : IDisposable
{
    ConnectionState State { get; }
    string? StatusDetail { get; }

    /// <summary>Changement d'état de connexion (Waiting / Connected / MissingDependency).</summary>
    event Action<ConnectionState, string?>? StateChanged;

    /// <summary>Nouvel instantané radio complet (pour rafraîchir les afficheurs).</summary>
    event Action<RadioSnapshot>? RadioSnapshotReceived;

    /// <summary>Changement atomique détecté (pour le journal horodaté).</summary>
    event Action<RadioChange>? RadioChanged;

    /// <summary>Nouveau contexte de vol (position, vitesses, altitudes, aéroport proche…).</summary>
    event Action<ContextSnapshot>? ContextReceived;

    /// <summary>Identité de l'avion (renvoyée à la connexion et à chaque changement d'appareil).</summary>
    event Action<AircraftSnapshot>? AircraftReceived;

    /// <summary>
    /// Météo ambiante + heure zoulou, toutes les quelques secondes (la météo bouge lentement).
    /// Alimente l'ATIS. Jamais levé si le simulateur ne connaît pas ces SimVars.
    /// </summary>
    event Action<WeatherSnapshot>? WeatherReceived;

    /// <summary>
    /// Fréquences COM LIVE d'un aéroport, en réponse à <see cref="RequestAirportFrequencies"/>.
    /// Levé sur le thread de pompage. Ne se déclenche pas si le simulateur ne gère pas la
    /// facility data (vieux SDK) — la couche appelante retombe alors sur les données CSV.
    /// </summary>
    event Action<AirportFacilityFrequencies>? AirportFrequenciesReceived;

    /// <summary>Démarre la boucle de connexion + pompage (non bloquant).</summary>
    void Start();

    /// <summary>Arrête proprement la boucle et libère SimConnect.</summary>
    void Stop();

    /// <summary>Allume/éteint le beacon (déclencheur de l'auto-pushback GSX). Thread-safe.</summary>
    void SetBeaconLight(bool on);

    // ------------------------------------------------------------------ intercepteur (IA)
    //
    // SEULE ÉCRITURE de l'application dans le monde du simulateur : un appareil créé à côté
    // du joueur, repositionné tant qu'il escorte, puis retiré. Tout est sans effet quand la
    // connexion est absente — jamais d'exception, jamais d'objet oublié.

    /// <summary>
    /// Crée un appareil IA. <paramref name="title"/> est le TITRE DE CONTENEUR exact
    /// (celui de l'aircraft.cfg) : s'il ne correspond à rien d'installé, le simulateur ne
    /// crée rien et <see cref="InterceptorCreated"/> ne se déclenche pas.
    /// </summary>
    void CreateInterceptor(string title, string tailNumber,
                           double lat, double lon, double altitudeFeet,
                           double headingTrueDeg, double airspeedKnots);

    /// <summary>Repositionne l'appareil créé (appelé plusieurs fois par seconde).</summary>
    void MoveInterceptor(uint objectId, double lat, double lon, double altitudeFeet,
                         double pitchDeg, double bankDeg, double headingTrueDeg,
                         double airspeedKnots,
                         double velocityEastFps, double velocityUpFps, double velocityNorthFps);

    /// <summary>Retire l'appareil du monde. Sans effet si l'identifiant est inconnu.</summary>
    void RemoveInterceptor(uint objectId);

    /// <summary>Identifiant de l'appareil créé, en réponse à <see cref="CreateInterceptor"/>.</summary>
    event Action<uint>? InterceptorCreated;

    /// <summary>
    /// Démarre le flux de formation : l'état du joueur à CHAQUE IMAGE, via
    /// <see cref="FormationTick"/>. À n'activer que le temps d'une escorte — c'est un flux
    /// dense, inutile le reste du temps.
    /// </summary>
    void StartFormationUpdates();

    /// <summary>Arrête le flux de formation.</summary>
    void StopFormationUpdates();

    /// <summary>Position/attitude du joueur, une fois par image pendant une escorte.</summary>
    event Action<FormationSnapshot>? FormationTick;

    /// <summary>
    /// Demande la liste des appareils présents dans un rayon (en mètres) autour du joueur.
    /// Réponse appareil par appareil via <see cref="NearbyAircraftSeen"/> — aucune réponse
    /// signifie qu'il n'y a aucun autre avion dans le monde, pas qu'une erreur s'est produite.
    /// </summary>
    void RequestNearbyAircraft(uint radiusMeters);

    /// <summary>
    /// Un appareil aperçu autour du joueur, avec son titre de conteneur exact. C'est la
    /// seule source de titres valides : ceux du disque sont empaquetés, et les titres
    /// devinés échouent silencieusement à la création.
    /// </summary>
    event Action<NearbyAircraft>? NearbyAircraftSeen;

    /// <summary>Relève l'état de vol des appareils environnants (même ensemble que l'identité).</summary>
    void RequestNearbyAircraftState(uint radiusMeters);

    /// <summary>
    /// Position d'un aéroport, telle que publiée par le simulateur. Faux si ce terrain n'est
    /// pas (encore) dans la bulle chargée autour de l'avion.
    /// </summary>
    bool TryGetAirportPosition(string icao, out double lat, out double lon);

    // ------------------------------------------------------------------ injection de trafic
    //
    // Appareils PILOTÉS PAR LE SIMULATEUR : on les fait naître, il les fait vivre. Il les fait
    // rouler, décoller, monter, descendre et atterrir avec son propre moteur — donc sans le
    // mouvement saccadé qu'on ne peut pas éviter quand c'est nous qui poussons un objet.

    /// <summary>Aéroports connus du simulateur autour de l'avion, avec leurs coordonnées.</summary>
    IReadOnlyList<(string Icao, double Lat, double Lon)> NearbyAirports();

    /// <summary>Fait naître un appareil garé à un terrain (le simulateur choisit le poste).</summary>
    void CreateParkedAircraft(string title, string tailNumber, string airportIcao);

    /// <summary>Fait naître un appareil sur un plan de vol, que le simulateur pilotera seul.</summary>
    void CreateEnrouteAircraft(string title, string tailNumber, int flightNumber,
                               string flightPlanPathNoExtension, double planPosition,
                               bool touchAndGo = false);

    /// <summary>Retire un appareil injecté.</summary>
    void RemoveAircraft(uint objectId);

    /// <summary>Identifiant d'un appareil de trafic créé.</summary>
    event Action<uint>? TrafficAircraftCreated;

    /// <summary>État de vol d'un appareil environnant, à rapprocher par identifiant d'objet.</summary>
    event Action<NearbyAircraftState>? NearbyAircraftStateSeen;

    /// <summary>
    /// Demande au simulateur les fréquences COM d'un aéroport (par ICAO). Thread-safe :
    /// l'ordre est mis en file et exécuté sur le thread de pompage. La réponse arrive via
    /// <see cref="AirportFrequenciesReceived"/>. Sans connexion (ou facility data indisponible),
    /// l'appel est ignoré silencieusement.
    /// </summary>
    void RequestAirportFrequencies(string icao);
}
