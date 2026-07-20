namespace FreqWatch.Sim;

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

    public void Start() { }
    public void Stop() { }
    public void SetBeaconLight(bool on) { }
    public void Dispose() { }
}
