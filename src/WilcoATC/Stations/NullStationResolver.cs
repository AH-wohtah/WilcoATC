using WilcoATC.Common;

namespace WilcoATC.Stations;

/// <summary>Résolveur inerte : ne renvoie jamais rien (comportement par défaut).</summary>
public sealed class NullStationResolver : IStationResolver
{
    public string? Resolve(double activeHz, double lat, double lon) => null;
    public StationInfo? ResolveStation(double activeHz, double lat, double lon) => null;
    public string? LookupAirportName(string icao) => null;
    public IReadOnlyList<AirportFrequency> ListFrequencies(string icao) => Array.Empty<AirportFrequency>();
    public double? FindFrequencyHz(string icao, ControllerType controller) => null;
    public double? FindNearestFrequencyHz(ControllerType controller, double lat, double lon, double maxKm) => null;
    public string? NearestControlledAirportIcao(double lat, double lon, bool includeSmallFields = false) => null;
    public (double Lat, double Lon)? AirportPosition(string icao) => null;
    public string? OperationalAirport(string? nearestIcao, double lat, double lon) => nearestIcao;
    public event Action<string>? FrequenciesUpdated { add { } remove { } }
    public int ImportOverlay(string csvPath) => 0;
}
