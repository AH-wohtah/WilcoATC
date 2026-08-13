using WilcoATC.Diagnostics;

namespace WilcoATC.Sim;

/// <summary>
/// Remplit le <see cref="SimTitleCatalog"/> en observant le simulateur : l'appareil piloté,
/// puis le trafic environnant, relevé régulièrement.
///
/// POURQUOI UN RELEVÉ PÉRIODIQUE plutôt qu'un seul au démarrage : au parking, le monde
/// autour de l'avion n'est pas encore chargé, et le trafic apparaît progressivement au fil
/// du vol. Un unique relevé au décollage ne verrait presque rien ; un relevé toutes les
/// minutes finit par croiser une bonne partie de la flotte installée.
///
/// Le relevé ne crée rien et ne modifie rien dans le simulateur : il ne fait que lire.
/// </summary>
public sealed class SimTitleCollector : IDisposable
{
    /// <summary>
    /// Rayon du relevé. 200 km attrape le trafic en route bien au-delà de ce que l'on voit,
    /// sans pour autant interroger la planète entière.
    /// </summary>
    private const uint ScanRadiusMeters = 200_000;

    /// <summary>Intervalle entre deux relevés. Le trafic change lentement.</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);

    private readonly ISimConnectService _sim;
    private readonly SimTitleCatalog _catalog;
    private readonly System.Threading.Timer _timer;

    /// <summary>Nombre d'appareils vus au dernier relevé — sert au diagnostic de trafic.</summary>
    private int _seenThisScan;
    private bool _connected;

    public SimTitleCollector(ISimConnectService sim, SimTitleCatalog catalog)
    {
        _sim = sim;
        _catalog = catalog;

        _sim.AircraftReceived += OnAircraft;
        _sim.NearbyAircraftSeen += OnNearby;
        _sim.StateChanged += OnState;

        _timer = new System.Threading.Timer(_ => Scan(), null, ScanInterval, ScanInterval);
    }

    /// <summary>Déclenche un relevé immédiat (au-delà du rythme périodique).</summary>
    public void ScanNow() => Scan();

    private void Scan()
    {
        if (!_connected) return;

        // Le compte du relevé PRÉCÉDENT est journalisé ici, une fois qu'il est complet : les
        // réponses arrivent appareil par appareil, sans message de fin. Zéro appareil est une
        // information à part entière — cela veut dire que le trafic du simulateur est éteint.
        int previous = Interlocked.Exchange(ref _seenThisScan, 0);
        FileLog.Write($"[titres] relevé du trafic : {previous} appareil(s) autour, " +
                      $"{_catalog.Count} titre(s) au catalogue");

        _sim.RequestNearbyAircraft(ScanRadiusMeters);
    }

    private void OnNearby(NearbyAircraft a)
    {
        Interlocked.Increment(ref _seenThisScan);
        _catalog.Observe(a.Title);
    }

    /// <summary>L'appareil du joueur : la source la plus sûre, disponible dès la connexion.</summary>
    private void OnAircraft(AircraftSnapshot a) => _catalog.Observe(a.Title);

    private void OnState(ConnectionState state, string? _)
    {
        bool wasConnected = _connected;
        _connected = state == ConnectionState.Connected;

        // Premier relevé peu après la connexion : le monde met un moment à se charger, mais
        // l'appareil du joueur, lui, est déjà là.
        if (_connected && !wasConnected)
            _timer.Change(TimeSpan.FromSeconds(20), ScanInterval);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _sim.AircraftReceived -= OnAircraft;
        _sim.NearbyAircraftSeen -= OnNearby;
        _sim.StateChanged -= OnState;
    }
}
