namespace FreqWatch.Atc;

/// <summary>
/// Vue consolidée des données de vol, assemblée par l'AtcController à partir des
/// événements de la couche SimConnect. C'est l'ENTRÉE du générateur ATC.
/// </summary>
public sealed record FlightSnapshot(
    string Callsign,             // indicatif (ATC ID / immatriculation), ou substitut
    string AircraftTitle,
    bool OnGround,
    double AltitudeMslFeet,
    double AltitudeAglFeet,
    double HeadingTrueDeg,
    double IasKnots,
    double GroundSpeedKnots,
    string Com1ActiveMhz,        // "118.700"
    double Com1ActiveHz,
    string? Station,             // station résolue (ex. "Paris CDG · TWR") ou null
    string? NearestAirportIcao,
    double Latitude,
    double Longitude);
