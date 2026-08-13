using WilcoATC.Common;

namespace WilcoATC.Stations;

/// <summary>
/// Une fréquence publiée d'un aéroport, pour l'affichage « toutes les fréquences du
/// terrain » de la fenêtre principale.
/// </summary>
/// <param name="Label">Libellé court affiché (« TWR », « GND », « ATIS »…).</param>
/// <param name="Type">Position de contrôle reconnue, ou <c>Unknown</c> pour du texte libre.</param>
/// <param name="Mhz">Fréquence en mégahertz.</param>
public sealed record AirportFrequency(string Label, ControllerType Type, double Mhz);


/// <summary>Station résolue à partir d'une fréquence : nom + type de contrôleur.</summary>
public sealed record StationInfo(string Name, ControllerType Controller);
