using FreqWatch.Common;

namespace FreqWatch.Stations;

/// <summary>Station résolue à partir d'une fréquence : nom + type de contrôleur.</summary>
public sealed record StationInfo(string Name, ControllerType Controller);
