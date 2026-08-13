namespace WilcoATC.Sim;

// Identifiants des DÉFINITIONS de données (une struct abonnée par définition).
internal enum DEFINITION
{
    RadioData = 0,   // fréquences COM (détection de changement via flag CHANGED)
    Context = 1,     // position + vitesses + altitudes (flux périodique 1 Hz)
    AircraftId = 2,  // identité de l'avion (chaînes ; renvoyée seulement si elle change)
    AircraftPerf = 3,// gabarit de l'avion (moteur, masse ; sert à classer VFR/IFR)
    AirportFreqs = 4,// définition FACILITY DATA : fréquences COM d'un aéroport (fix live)
    Weather = 5,     // météo ambiante + heure zoulou (source de l'ATIS)
    AiPosition = 6,  // position imposée à un objet IA (l'intercepteur) — ÉCRITURE
    Formation = 7,   // état du joueur à CHAQUE IMAGE, le temps d'une escorte

    // Avions présents AUTOUR du joueur, lus par type d'objet. C'est la seule source fiable de
    // TITRES DE CONTENEUR : dans Microsoft Flight Simulator 2024 les aircraft.cfg sont
    // empaquetés, donc introuvables sur le disque — mais le simulateur, lui, nous donne le
    // titre exact de chaque appareil qu'il a lui-même fait naître.
    NearbyAircraft = 8,

    // État de vol des MÊMES appareils. Définition séparée de l'identité : mêler chaînes et
    // flottants dans une même définition rend le marshalling fragile. Les deux relevés se
    // rapprochent par identifiant d'objet.
    NearbyState = 9,
}

// Identifiants des REQUÊTES.
internal enum REQUEST
{
    RadioData = 0,
    Context = 1,
    AircraftId = 2,
    AirportList = 3, // abonnement aux aéroports du cache SimConnect (pas de définition)
    AircraftPerf = 4,
    Weather = 5,
    CreateInterceptor = 6, // création de l'avion de chasse (réponse : OnRecvAssignedObjectId)
    RemoveInterceptor = 7,
    Formation = 8,         // flux par image, actif seulement pendant une interception
    NearbyAircraft = 9,    // relevé ponctuel des avions autour (réponse : OnRecvSimobjectDataBytype)
    NearbyState = 10,      // état de vol des mêmes appareils, relevé conjointement

    // Injection de trafic : appareils PILOTÉS PAR LE SIMULATEUR (plan de vol ou parking).
    // Rien à voir avec l'intercepteur, que l'on déplace nous-même.
    CreateTraffic = 11,
    RemoveTraffic = 12,
}

// Événements client mappés vers des events du simu (envoi d'ordres au simu).
internal enum EVENT
{
    BeaconSet = 0, // "BEACON_LIGHTS_SET" — sert à déclencher l'auto-pushback GSX

    // GEL de la physique d'un objet IA. Sans eux, le simulateur continue d'intégrer sa
    // propre mécanique du vol entre nos écritures de position : il tombe, dérive, se
    // redresse — et se bat contre nous à chaque image. Gelé, l'appareil ne bouge plus QUE
    // là où on le place, et le vol en formation devient net.
    FreezeLatLon = 1,    // "FREEZE_LATITUDE_LONGITUDE_SET"
    FreezeAltitude = 2,  // "FREEZE_ALTITUDE_SET"
    FreezeAttitude = 3,  // "FREEZE_ATTITUDE_SET"
}

// Groupe de notification (priorité) pour TransmitClientEvent.
internal enum NOTIFY_GROUP : uint
{
    Priority = 1, // = SIMCONNECT_GROUP_PRIORITY_HIGHEST avec le flag GROUPID_IS_PRIORITY
}
