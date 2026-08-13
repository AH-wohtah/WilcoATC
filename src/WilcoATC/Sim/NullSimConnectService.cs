namespace WilcoATC.Sim;

/// <summary>
/// Implémentation inerte de <see cref="ISimConnectService"/>.
/// Sert uniquement d'échafaudage pour l'aperçu du concepteur XAML (design-time).
/// </summary>
public sealed class NullSimConnectService : ISimConnectService
{
    public ConnectionState State => ConnectionState.Waiting;
    public string? StatusDetail => null;

    public event Action<ConnectionState, string?>? StateChanged { add { } remove { } }
    public event Action<RadioSnapshot>? RadioSnapshotReceived { add { } remove { } }
    public event Action<RadioChange>? RadioChanged { add { } remove { } }
    public event Action<ContextSnapshot>? ContextReceived { add { } remove { } }
    public event Action<AircraftSnapshot>? AircraftReceived { add { } remove { } }
    public event Action<WeatherSnapshot>? WeatherReceived { add { } remove { } }
    public event Action<AirportFacilityFrequencies>? AirportFrequenciesReceived { add { } remove { } }

    public event Action<uint>? InterceptorCreated { add { } remove { } }
    public event Action<FormationSnapshot>? FormationTick { add { } remove { } }
    public event Action<NearbyAircraft>? NearbyAircraftSeen { add { } remove { } }
    public event Action<NearbyAircraftState>? NearbyAircraftStateSeen { add { } remove { } }

    public void StartFormationUpdates() { }
    public void StopFormationUpdates() { }
    public void RequestNearbyAircraft(uint radiusMeters) { }
    public void RequestNearbyAircraftState(uint radiusMeters) { }
    public bool TryGetAirportPosition(string icao, out double lat, out double lon)
    {
        lat = lon = 0;
        return false;
    }

    // Sans simulateur, il n'y a aucun monde où injecter du trafic.
    public IReadOnlyList<(string Icao, double Lat, double Lon)> NearbyAirports() => Array.Empty<(string, double, double)>();
    public void CreateParkedAircraft(string title, string tailNumber, string airportIcao) { }
    public void CreateEnrouteAircraft(string title, string tailNumber, int flightNumber,
                                      string flightPlanPathNoExtension, double planPosition,
                                      bool touchAndGo = false) { }
    public void RemoveAircraft(uint objectId) { }
    public event Action<uint>? TrafficAircraftCreated { add { } remove { } }

    public void Start() { }
    public void Stop() { }
    public void SetBeaconLight(bool on) { }
    public void RequestAirportFrequencies(string icao) { }

    // Sans simulateur, il n'y a personne à intercepter : tout est inerte.
    public void CreateInterceptor(string title, string tailNumber, double lat, double lon,
                                  double altitudeFeet, double headingTrueDeg, double airspeedKnots) { }
    public void MoveInterceptor(uint objectId, double lat, double lon, double altitudeFeet,
                                double pitchDeg, double bankDeg, double headingTrueDeg,
                                double airspeedKnots,
                                double velocityEastFps, double velocityUpFps, double velocityNorthFps) { }
    public void RemoveInterceptor(uint objectId) { }

    public void Dispose() { }
}
