namespace WilcoATC.Sim;

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
    double NearestAirportDistanceMeters,
    bool InFlightSession = true);    // vrai quand on est aux commandes (cockpit/vol), faux au menu / carte du monde

/// <summary>Précipitations, décodées depuis la SimVar « AMBIENT PRECIP STATE ».</summary>
public enum PrecipKind
{
    Unknown, // valeur hors nomenclature -> on n'annonce rien plutôt que d'inventer
    None,
    Rain,
    Snow,
}

/// <summary>
/// Météo relevée À LA POSITION DE L'AVION, plus l'heure zoulou du simulateur. C'est la
/// matière première de l'ATIS.
///
/// Ce n'est PAS la météo du terrain : SimConnect ne sait mesurer qu'à l'endroit où se
/// trouve l'appareil. La remise au niveau du sol (vent de surface, température) appartient
/// à la couche ATIS — cette structure reste une mesure brute.
/// </summary>
public sealed record WeatherSnapshot(
    double WindDirectionTrueDeg,   // d'où vient le vent, en degrés VRAIS
    double WindSpeedKnots,
    double TemperatureC,
    double VisibilityMeters,
    double SeaLevelPressureHpa,    // déjà réduite au niveau de la mer par le simulateur (QNH)
    double MagneticVariationDeg,   // déclinaison, Est positif (magnétique = vrai - déclinaison)
    PrecipKind Precipitation,
    TimeSpan ZuluTime);

/// <summary>
/// Motorisation, décodée depuis la SimVar « ENGINE TYPE ». C'est le premier critère de
/// classement de l'appareil : le monde du piston est celui du VFR.
/// </summary>
public enum EngineKind
{
    Unknown = -1,
    Piston = 0,
    Jet = 1,
    None = 2,
    HelicopterTurbine = 3,
    Unsupported = 4,
    Turboprop = 5,
}

/// <summary>
/// Identité de l'avion courant, complétée par son GABARIT (motorisation, masse maximale…).
/// Les champs de gabarit ont une valeur par défaut : tant que SimConnect ne les a pas
/// renvoyés, l'appareil est simplement « non classé » et rien n'en dépend de façon dure.
/// </summary>
/// <summary>
/// État du joueur à CHAQUE IMAGE, pendant une escorte uniquement. Sert à placer l'appareil
/// qui vole en formation : c'est la cadence, et non la méthode, qui fait la différence entre
/// un avion qui vole à côté de vous et un avion qui se téléporte.
/// </summary>
public sealed record FormationSnapshot(
    double Latitude, double Longitude, double AltitudeFeet,
    double HeadingTrueDeg, double PitchDeg, double BankDeg, double AirspeedTrueKnots,
    double VelocityEastFps, double VelocityUpFps, double VelocityNorthFps);

/// <summary>
/// Un appareil aperçu autour du joueur. Son <paramref name="Title"/> est un TITRE DE
/// CONTENEUR authentique — le simulateur s'en sert en ce moment même — donc utilisable tel
/// quel pour créer un appareil, ce qu'aucun titre deviné ne garantit.
/// </summary>
public sealed record NearbyAircraft(
    uint ObjectId, string Title, string AtcType, string AtcModel,
    string TailNumber = "", string Airline = "", string FlightNumber = "");

/// <summary>
/// Où en est un appareil environnant. Rapproché de <see cref="NearbyAircraft"/> par
/// <see cref="ObjectId"/> : c'est le couple identité + état qui permet de dire « Air France
/// 1234, autorisé atterrissage piste 27 droite ».
/// </summary>
public sealed record NearbyAircraftState(
    uint ObjectId,
    double Latitude, double Longitude,
    double AltitudeMslFeet, double AltitudeAglFeet,
    double HeadingTrueDeg, double GroundSpeedKnots, double VerticalSpeedFpm,
    bool OnGround);

public sealed record AircraftSnapshot(
    string Title, string AtcType, string AtcModel, string TailNumber,
    EngineKind Engine = EngineKind.Unknown,
    int EngineCount = 0,
    double MaxGrossWeightLbs = 0,
    bool GearRetractable = false,
    double DesignCruiseAltFeet = 0);

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

/// <summary>Une fréquence COM live d'un aéroport, telle que publiée par le simulateur.</summary>
/// <param name="Name">Nom du canal (« MANILA TWR », « GROUND »…).</param>
/// <param name="Type">Type numérique MSFS (0 none, 5 gnd, 6 twr, 7 clr, 8 app…).</param>
/// <param name="Mhz">Fréquence en mégahertz.</param>
public sealed record SimComFrequency(string Name, int Type, double Mhz);

/// <summary>Réponse « facility data » : toutes les fréquences COM d'un aéroport (par ICAO).</summary>
public sealed record AirportFacilityFrequencies(string Icao, IReadOnlyList<SimComFrequency> Frequencies);
