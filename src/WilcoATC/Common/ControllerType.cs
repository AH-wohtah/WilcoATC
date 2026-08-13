namespace WilcoATC.Common;

/// <summary>Type de contrôleur ATC, déduit du type de fréquence de la station.</summary>
public enum ControllerType
{
    Clearance,
    Ground,
    Tower,
    Approach,
    Departure,
    Center,
    Atis,
    Unknown,
}
