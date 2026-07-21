using FreqWatch.Common;

namespace FreqWatch.Stations;

/// <summary>Résolveur inerte : ne renvoie jamais rien (comportement par défaut).</summary>
public sealed class NullStationResolver : IStationResolver
{
    public string? Resolve(double activeHz, double lat, double lon) => null;
    public StationInfo? ResolveStation(double activeHz, double lat, double lon) => null;
    public string? LookupAirportName(string icao) => null;
    public double? FindFrequencyHz(string icao, ControllerType controller) => null;
    public double? FindNearestFrequencyHz(ControllerType controller, double lat, double lon, double maxKm) => null;
    public string? NearestControlledAirportIcao(double lat, double lon) => null;
    public (double Lat, double Lon)? AirportPosition(string icao) => null;
}
