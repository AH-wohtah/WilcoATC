using System.Runtime.InteropServices;

namespace FreqWatch.Sim;

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
