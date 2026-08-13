using System.Runtime.InteropServices;

namespace WilcoATC.Sim;

// ============================================================================
//  Structures marshalées par SimConnect.
//
//  IMPORTANT : l'ordre des champs DOIT correspondre EXACTEMENT à l'ordre des
//  appels AddToDataDefinition(...) dans SimConnectService.
// ============================================================================

// Fréquences COM — toutes en Hz (FLOAT64). Voir FrequencyFormatter pour l'unité.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct RadioData
{
    public double Com1ActiveHz;   // "COM ACTIVE FREQUENCY:1"  (Hz)
    public double Com1StandbyHz;  // "COM STANDBY FREQUENCY:1" (Hz)
    public double Com2ActiveHz;   // "COM ACTIVE FREQUENCY:2"  (Hz)
    public double Com2StandbyHz;  // "COM STANDBY FREQUENCY:2" (Hz)
    public double Com1Transmit;   // "COM TRANSMIT:1"          (Bool 0/1)
    public double Com2Transmit;   // "COM TRANSMIT:2"          (Bool 0/1)
}

// Contexte de vol : position, altitudes, vitesses, cap, squawk. Tout en FLOAT64.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ContextData
{
    public double Latitude;         // "PLANE LATITUDE"             (degrés)
    public double Longitude;        // "PLANE LONGITUDE"            (degrés)
    public double AltitudeMslFeet;  // "PLANE ALTITUDE"            (pieds, MSL)
    public double AltitudeAglFeet;  // "PLANE ALT ABOVE GROUND"    (pieds, sol)
    public double HeadingTrueDeg;   // "PLANE HEADING DEGREES TRUE" (degrés)
    public double IasKnots;         // "AIRSPEED INDICATED"        (nœuds)
    public double GroundSpeedKnots; // "GROUND VELOCITY"           (nœuds)
    public double VerticalSpeedFpm; // "VERTICAL SPEED"            (pieds/min)
    public double OnGround;         // "SIM ON GROUND"             (Bool 0/1)
    public double TransponderBcd;   // "TRANSPONDER CODE:1"        (BCO16 / BCD)
    public double ParkingBrake;     // "BRAKE PARKING INDICATOR"   (Bool 0/1)
    public double CameraState;      // "CAMERA STATE"              (Enum : 2-10 = en cabine/vol, ≥11 = menus/carte du monde)
}

// Météo ambiante + heure zoulou : source de l'ATIS. Tout en FLOAT64.
//
// ATTENTION : ces valeurs sont relevées LÀ OÙ EST L'AVION (SimConnect ne sait pas mesurer
// ailleurs). À 30 000 ft c'est le courant-jet et -50 °C, pas le vent de piste : la remise
// aux conditions du terrain est faite par AtisSurface, pas ici.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WeatherData
{
    public double WindDirectionDeg;    // "AMBIENT WIND DIRECTION"  (degrés, VRAIS — d'où vient le vent)
    public double WindSpeedKnots;      // "AMBIENT WIND VELOCITY"   (nœuds)
    public double TemperatureC;        // "AMBIENT TEMPERATURE"     (°C)
    public double VisibilityMeters;    // "AMBIENT VISIBILITY"      (mètres)
    public double SeaLevelPressureMb;  // "SEA LEVEL PRESSURE"      (millibars = hPa -> QNH)
    public double MagVarDeg;           // "MAGVAR"                  (déclinaison, Est positif)
    public double PrecipState;         // "AMBIENT PRECIP STATE"    (masque : 2 aucune, 4 pluie, 8 neige)
    public double ZuluTimeSeconds;     // "ZULU TIME"               (secondes depuis minuit UTC)
}

/// <summary>
/// État de l'avion du JOUEUR, échantillonné à CHAQUE IMAGE pendant une escorte.
///
/// Le flux de contexte ordinaire tourne à 1 Hz : suffisant pour la logique ATC, désastreux
/// pour un vol en formation — à 250 nœuds, une seconde représente 130 mètres, et l'escorte
/// se déplace par bonds. Ce flux-ci n'est demandé QUE pendant une interception, puis coupé.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FormationData
{
    public double Latitude;      // "PLANE LATITUDE"
    public double Longitude;     // "PLANE LONGITUDE"
    public double AltitudeFeet;  // "PLANE ALTITUDE"
    public double HeadingTrue;   // "PLANE HEADING DEGREES TRUE"
    public double PitchDeg;      // "PLANE PITCH DEGREES"   (positif = nez bas côté SimConnect)
    public double BankDeg;       // "PLANE BANK DEGREES"
    public double AirspeedTrue;  // "AIRSPEED TRUE"

    // Vecteur vitesse dans le repère MONDE (pieds/seconde) : X est, Y haut, Z nord. C'est
    // lui qui fait AVANCER l'appareil entre deux corrections de position — sans vitesse, le
    // simulateur n'a aucune raison d'interpoler quoi que ce soit et n'affiche qu'un modèle
    // déplacé de force, d'où l'impression d'une image qui glisse.
    public double VelocityEast;  // "VELOCITY WORLD X"
    public double VelocityUp;    // "VELOCITY WORLD Y"
    public double VelocityNorth; // "VELOCITY WORLD Z"
}

/// <summary>
/// Position IMPOSÉE à un objet IA (l'intercepteur). C'est la seule structure que
/// l'application ÉCRIT dans le simulateur : tout le reste est en lecture.
///
/// On pilote l'appareil en le TÉLÉPORTANT à 5 Hz plutôt qu'en le laissant voler : un objet
/// IA suit un plan de vol, il ne sait pas tenir une formation sur un avion qui manœuvre.
/// Recalculer sa place à côté du joueur et l'y poser donne un vol en formation stable,
/// c'est la méthode qu'emploient les outils d'interception existants.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct AiPositionData
{
    public double Latitude;      // "PLANE LATITUDE"        (degrés)
    public double Longitude;     // "PLANE LONGITUDE"       (degrés)
    public double AltitudeFeet;  // "PLANE ALTITUDE"        (pieds)
    public double PitchDeg;      // "PLANE PITCH DEGREES"   (degrés)
    public double BankDeg;       // "PLANE BANK DEGREES"    (degrés)
    public double HeadingTrue;   // "PLANE HEADING DEGREES TRUE" (degrés)
    public double OnGround;      // "SIM ON GROUND"         (0 = en vol)
    public double AirspeedTrue;  // "AIRSPEED TRUE"         (nœuds)

    // Même vecteur vitesse que le joueur : l'escorte vole en parallèle, donc elle a
    // exactement la même. C'est ce qui la fait AVANCER entre nos corrections, avec ses
    // animations et son inertie, au lieu d'être posée image par image.
    public double VelocityEast;  // "VELOCITY WORLD X"      (pieds/seconde)
    public double VelocityUp;    // "VELOCITY WORLD Y"
    public double VelocityNorth; // "VELOCITY WORLD Z"
}

// Gabarit de l'avion : variables NUMÉRIQUES. Servent à CLASSER l'appareil (léger,
// turbopropulseur, jet d'affaires, avion de ligne) et donc à en déduire les règles de vol
// par défaut — un Cessna ne vole pas aux mêmes règles qu'un A320.
//
// Définition SÉPARÉE des chaînes : mélanger STRINGxxx et FLOAT64 dans une même définition
// rend le marshalling fragile sans rien apporter.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct AircraftPerfData
{
    public double EngineType;        // "ENGINE TYPE"          (Enum : 0 piston, 1 jet, 2 aucun, 3 turbine hélico, 4 non géré, 5 turboprop)
    public double NumberOfEngines;   // "NUMBER OF ENGINES"    (Number)
    public double MaxGrossWeightLbs; // "MAX GROSS WEIGHT"     (livres)
    public double GearRetractable;   // "IS GEAR RETRACTABLE"  (Bool 0/1)
    public double DesignCruiseAlt;   // "DESIGN CRUISE ALT"    (pieds)
}

// ---------------------------------------------------------------------------
//  FACILITY DATA (fréquences COM live d'un aéroport). Voir RequestFacilityData.
//  L'ordre des champs DOIT correspondre à l'ordre des AddToFacilityDefinition,
//  et le type enregistré via RegisterFacilityDataDefineStruct.
// ---------------------------------------------------------------------------

// Nœud AIRPORT : on n'en lit que la position (les fréquences arrivent en nœuds FREQUENCY
// séparés). Enregistré uniquement pour que le nœud parent se marshalle proprement.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct FacilityAirportData
{
    public double Latitude;   // "LATITUDE"  (degrés)
    public double Longitude;  // "LONGITUDE" (degrés)
}

// Nœud FREQUENCY : une fréquence COM publiée par le simulateur (source de vérité, add-ons
// compris). Champs SDK : TYPE (enum), FREQUENCY (Hz, INT32), NAME (CHAR[64]).
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
internal struct FacilityFrequencyData
{
    public int Type;     // "TYPE"      (0 none,1 atis,5 gnd,6 twr,7 clr,8 app,9 dep,10 ctr…)
    public int FreqHz;   // "FREQUENCY" (Hz, entier)

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string Name;  // "NAME"      (nom du canal, ex. « MANILA TWR »)
}

// Identité de l'avion : variables de type CHAÎNE.
// -> nécessite CharSet.Ansi + MarshalAs ByValTStr, avec SizeConst = taille du
//    SIMCONNECT_DATATYPE.STRINGxxx correspondant.
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
internal struct AircraftIdData
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;    // "TITLE"     (titre de la config, ex. "Airbus A320neo Asobo")

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcType;  // "ATC TYPE"  (constructeur)

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcModel; // "ATC MODEL" (modèle)

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string AtcId;    // "ATC ID"    (immatriculation / tail number)
}

/// <summary>
/// Identité d'un avion PRÉSENT AUTOUR DU JOUEUR, relevée par type d'objet.
///
/// POURQUOI CETTE LECTURE EXISTE : créer un appareil demande son TITRE DE CONTENEUR exact,
/// et un titre inexact échoue sans le moindre indice utilisable. Or, dans Microsoft Flight
/// Simulator 2024, les aircraft.cfg sont empaquetés dans des archives compressées : aucun
/// titre n'est lisible sur le disque, et les deviner ne marche pas. Le simulateur, lui,
/// connaît le titre de chaque appareil qu'il a fait naître — il suffit de le lui demander.
/// Les titres ainsi relevés sont valides par construction, puisqu'ils sont déjà en vol.
///
/// CHAÎNES UNIQUEMENT, délibérément : mêler STRINGxxx et FLOAT64 dans une même définition
/// rend le marshalling fragile, et la position des autres appareils ne nous sert à rien.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
internal struct NearbyAircraftData
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;    // "TITLE"     — c'est CE champ que l'on cherche

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcType;  // "ATC TYPE"  (constructeur, ex. « Boeing »)

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcModel; // "ATC MODEL" (modèle, ex. « B738 »)

    // De quoi PRONONCER un indicatif. « Air France » + « 1234 » se dit « Air France 1234 » ;
    // à défaut de compagnie, on épelle l'immatriculation. Sans ça, on saurait qu'un appareil
    // est en finale sans pouvoir s'adresser à lui.
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string AtcId;           // "ATC ID"            (immatriculation)

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string AtcAirline;      // "ATC AIRLINE"       (compagnie, ex. « Air France »)

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string AtcFlightNumber; // "ATC FLIGHT NUMBER" (numéro de vol)
}

/// <summary>
/// État de vol d'un appareil environnant. Séparé de l'identité — mêler STRINGxxx et FLOAT64
/// dans une même définition rend le marshalling fragile — et relevé par la même requête « par
/// type d'objet », ce qui permet de rapprocher les deux par identifiant d'objet.
///
/// C'est ce qui rend l'ATC capable de parler du monde RÉEL : savoir qu'un appareil descend à
/// mille pieds, aligné sur la piste, à six milles du seuil, c'est savoir qu'il faut lui
/// délivrer son autorisation d'atterrissage maintenant.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct NearbyStateData
{
    public double Latitude;        // "PLANE LATITUDE"           (degrés)
    public double Longitude;       // "PLANE LONGITUDE"          (degrés)
    public double AltitudeFeet;    // "PLANE ALTITUDE"           (pieds, MSL)
    public double AltitudeAglFeet; // "PLANE ALT ABOVE GROUND"   (pieds, sol)
    public double HeadingTrue;     // "PLANE HEADING DEGREES TRUE" (degrés)
    public double GroundSpeedKnots;// "GROUND VELOCITY"          (nœuds)
    public double VerticalSpeedFpm;// "VERTICAL SPEED"           (pieds/minute)
    public double OnGround;        // "SIM ON GROUND"            (0 = en vol)
}
