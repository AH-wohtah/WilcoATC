using WilcoATC.Atc.Planning;
using WilcoATC.Common;
using WilcoATC.Settings;
using WilcoATC.Sim;
using WilcoATC.Stations;

namespace WilcoATC.Atc.Context;

/// <summary>
/// Assemble le <see cref="FlightContext"/> courant à partir des derniers instantanés
/// SimConnect (contexte/radio/avion) et de la résolution de station. Gère l'état de
/// phase (via l'estimateur) et l'avancement « pushback autorisé » piloté par le cerveau.
///
/// Un OVERRIDE manuel du contrôleur permet de tester la validation sans les CSV OurAirports.
/// </summary>
public sealed class FlightContextProvider
{
    private readonly IStationResolver _stations;
    private readonly SettingsService _settings;
    private readonly FlightPlanStore _plans;
    private readonly FlightPhaseEstimator _estimator = new();

    private ContextSnapshot? _context;
    private RadioSnapshot? _radio;
    private AircraftSnapshot? _aircraft;

    private FlightPhase _sensorPhase = FlightPhase.Unknown;
    private bool _pushbackGranted;
    private ControllerType? _controllerOverride;

    // Overrides DEBUG (Mode Test) : forcent la phase / le flag « déjà été en l'air » pour
    // placer l'app dans un état précis avant un test ciblé. null = automatique (capteurs).
    private FlightPhase? _phaseOverride;
    private bool? _airborneOverride;

    public FlightContextProvider(IStationResolver stations, SettingsService settings, FlightPlanStore plans)
    {
        _stations = stations;
        _settings = settings;
        _plans = plans;
    }

    /// <summary>
    /// Règles de vol effectives, recalculées à la demande : le réglage, le plan de vol et
    /// l'appareil peuvent tous changer en cours de session (import SimBrief, changement
    /// d'avion dans le simulateur). Le calcul est trivial, inutile de le mettre en cache.
    /// </summary>
    public FlightRulesDecision RulesDecision
        => FlightRulesResolver.Resolve(_settings.Current.FlightRules, _plans.Current, _aircraft);

    /// <summary>Règles de vol effectives. Raccourci de <see cref="RulesDecision"/>.</summary>
    public FlightRules Rules => RulesDecision.Rules;

    /// <summary>Dernier instantané avion reçu (gabarit compris), ou null. Lecture seule.</summary>
    public AircraftSnapshot? Aircraft => _aircraft;

    /// <summary>Phase effective courante (override debug > « pushback autorisé » > capteur). Pour le debug UI.</summary>
    public FlightPhase EffectivePhase
    {
        get
        {
            if (_phaseOverride is { } forced) return forced;
            var phase = _sensorPhase;
            if (_pushbackGranted && phase == FlightPhase.Parked) phase = FlightPhase.Pushback;
            return phase;
        }
    }

    /// <summary>A-t-on déjà été clairement en l'air pendant cette session ? (override debug possible). Pour le debug UI.</summary>
    public bool HasBeenAirborne => _airborneOverride ?? _estimator.HasBeenAirborne;

    public void OnContext(ContextSnapshot c)
    {
        _context = c;
        _sensorPhase = _estimator.Estimate(c);
        // Dès que l'avion bouge (ou décolle), l'état "pushback autorisé" n'a plus lieu d'être.
        if (_sensorPhase != FlightPhase.Parked) _pushbackGranted = false;
    }

    public void OnRadio(RadioSnapshot r) => _radio = r;

    /// <summary>
    /// Météo du simulateur. Seul le VENT nous intéresse ici : c'est lui qui désigne la piste
    /// en service, comme pour un vrai contrôleur.
    /// </summary>
    public void OnWeather(WeatherSnapshot w) => _weather = w;

    private WeatherSnapshot? _weather;
    public void OnAircraft(AircraftSnapshot a) => _aircraft = a;

    public void Reset()
    {
        _estimator.Reset();
        _pushbackGranted = false;
        _sensorPhase = FlightPhase.Unknown;
    }

    /// <summary>Le cerveau signale que le pushback vient d'être accordé (avance la phase).</summary>
    public void MarkPushbackGranted() => _pushbackGranted = true;

    /// <summary>Override manuel du contrôleur (null = automatique depuis la fréquence).</summary>
    public void SetControllerOverride(ControllerType? controller) => _controllerOverride = controller;

    /// <summary>Force la phase courante (DEBUG / Mode Test). null = automatique (capteurs).</summary>
    public void SetPhaseOverride(FlightPhase? phase) => _phaseOverride = phase;

    /// <summary>Force le flag « déjà été en l'air » (DEBUG / Mode Test). null = automatique.</summary>
    public void SetHasBeenAirborneOverride(bool? value) => _airborneOverride = value;

    public FlightContext Current()
    {
        var c = _context;
        var a = _aircraft;

        FlightPhase phase = EffectivePhase;

        ControllerType controller = _controllerOverride ?? ResolveController();

        string callsign = a?.TailNumber ?? "";
        if (string.IsNullOrWhiteSpace(callsign)) callsign = "Aircraft";

        var station = (_radio is not null && c is not null)
            ? _stations.ResolveStation(_radio.Com1ActiveHz, c.Latitude, c.Longitude)
            : null;

        var rules = RulesDecision;

        return new FlightContext(
            Phase: phase,
            Controller: controller,
            OnGround: c?.OnGround ?? true,
            Callsign: callsign,
            StationName: station?.Name ?? "",
            AirportIcao: c is null ? null : _stations.OperationalAirport(c.NearestAirportIcao, c.Latitude, c.Longitude),
            Rules: rules.Rules,
            Class: rules.Class,
            HeadingDeg: c?.HeadingTrueDeg ?? 0,
            WindFromDeg: _weather?.WindDirectionTrueDeg ?? 0,
            WindKnots: _weather?.WindSpeedKnots ?? 0);
    }

    private ControllerType ResolveController()
    {
        var c = _context; var r = _radio;
        if (c is null || r is null) return ControllerType.Unknown;
        return _stations.ResolveStation(r.Com1ActiveHz, c.Latitude, c.Longitude)?.Controller ?? ControllerType.Unknown;
    }
}
