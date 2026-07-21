namespace FreqWatch.Sim;

// ============================================================================
//  DTO exposés à l'UI. Aucun type SimConnect ne "fuit" au-delà de cette couche.
// ============================================================================

/// <summary>Instantané complet de l'état radio (fréquences en Hz, TX en booléen).</summary>
public sealed record RadioSnapshot(
    double Com1ActiveHz, double Com1StandbyHz,
    double Com2ActiveHz, double Com2StandbyHz,
    bool Com1Transmit, bool Com2Transmit);

/// <summary>Instantané de contexte de vol (déjà décodé/normalisé).</summary>
public sealed record ContextSnapshot(
    double Latitude,
    double Longitude,
    double AltitudeMslFeet,
    double AltitudeAglFeet,
    double HeadingTrueDeg,
    double IasKnots,
    double GroundSpeedKnots,
    double VerticalSpeedFpm,
    bool OnGround,
    bool ParkingBrake,
    int TransponderCode,
    string? NearestAirportIcao,      // ICAO de l'aéroport le plus proche (cache SimConnect), ou null
    double NearestAirportDistanceMeters);

/// <summary>Identité de l'avion courant.</summary>
public sealed record AircraftSnapshot(
    string Title, string AtcType, string AtcModel, string TailNumber);

/// <summary>Nature d'un changement radio, pour le journal et la coloration.</summary>
public enum RadioChangeKind
{
    Frequency, // une fréquence a changé
    Transmit,  // la radio émettrice a changé
    Initial,   // premier état reçu après (re)connexion
}

/// <summary>Un changement atomique à journaliser.</summary>
public sealed record RadioChange(
    string RadioLabel,
    string FieldLabel,
    string NewValue,
    RadioChangeKind Kind);
