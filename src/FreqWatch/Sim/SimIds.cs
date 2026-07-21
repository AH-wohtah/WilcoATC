namespace FreqWatch.Sim;

// Identifiants des DÉFINITIONS de données (une struct abonnée par définition).
internal enum DEFINITION
{
    RadioData = 0,   // fréquences COM (détection de changement via flag CHANGED)
    Context = 1,     // position + vitesses + altitudes (flux périodique 1 Hz)
    AircraftId = 2,  // identité de l'avion (chaînes ; renvoyée seulement si elle change)
}

// Identifiants des REQUÊTES.
internal enum REQUEST
{
    RadioData = 0,
    Context = 1,
    AircraftId = 2,
    AirportList = 3, // abonnement aux aéroports du cache SimConnect (pas de définition)
}

// Événements client mappés vers des events du simu (envoi d'ordres au simu).
internal enum EVENT
{
    BeaconSet = 0, // "BEACON_LIGHTS_SET" — sert à déclencher l'auto-pushback GSX
}

// Groupe de notification (priorité) pour TransmitClientEvent.
internal enum NOTIFY_GROUP : uint
{
    Priority = 1, // = SIMCONNECT_GROUP_PRIORITY_HIGHEST avec le flag GROUPID_IS_PRIORITY
}
