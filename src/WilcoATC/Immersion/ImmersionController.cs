using System.Diagnostics;
using WilcoATC.Atc;
using WilcoATC.Atc.Context;
using WilcoATC.Audio;
using WilcoATC.Common;
using WilcoATC.Settings;
using WilcoATC.Sim;

namespace WilcoATC.Immersion;

/// <summary>
/// Orchestre les trois briques d'immersion, branchées sur SimConnect :
///  • COPILOTE : annonces (80 kt, V1, rotation, vario positif, altitudes, minimums…) et
///    checklists aux transitions de phase — voix SÈCHE (interphone, pas d'effet radio) ;
///  • TRAFIC AMBIANT : d'autres équipages/le contrôle sur la fréquence — avec effet radio,
///    volume réduit, et JAMAIS par-dessus l'ATC (abandonné si le canal voix est occupé) ;
///  • CABINE : packs de sons déclenchés par la phase de vol — lecteur séparé, se superpose
///    volontairement à la radio.
///
/// Chaque brique s'active/se désactive indépendamment dans les réglages.
/// </summary>
public sealed class ImmersionController : IDisposable
{
    private readonly ISimConnectService _sim;
    private readonly FlightContextProvider _flight;
    private readonly ITtsEngine _tts;
    private readonly VoiceBus _voice;
    private readonly CabinAudioPlayer _cabinPlayer;
    private readonly CabinSoundPackRepository _packs;
    private readonly SettingsService _settings;
    private readonly LanguageResolver _language;
    private readonly VoicePicker _picker;

    private readonly CopilotDirector _copilot = new();

    /// <summary>
    /// Trafic d'ambiance. On lui donne l'indicatif du joueur pour qu'il ne l'attribue JAMAIS
    /// à l'un de ses équipages : deux avions du même nom sur la fréquence, et le pilote se
    /// fait répondre à sa place.
    /// </summary>
    private readonly ChatterDirector _chatter = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _rng = new();

    /// <summary>
    /// Interrupteur GÉNÉRAL des packs de sons de cabine : la fonction n'est pas livrée
    /// (« coming soon » dans les réglages). Tant qu'il vaut <c>false</c>, aucun son de
    /// cabine n'est joué, même si <c>CabinEnabled</c> traîne à true dans un settings.json
    /// existant. Le code reste en place : repasser à <c>true</c> réactive tout.
    /// </summary>
    public const bool CabinPacksAvailable = false;

    /// <summary>Vitesse de rotation typique d'un avion léger de tourisme (kt).</summary>
    private const int LightRotateKnots = 55;

    private double _lastTickSeconds;
    private FlightPhase _lastCabinPhase = FlightPhase.Unknown;

    /// <summary>
    /// Y a-t-il du VRAI trafic à faire parler en ce moment ? Fourni par le directeur de
    /// trafic ; null quand la fonction n'est pas câblée. Voir <see cref="RunChatter"/>.
    /// </summary>
    private readonly Func<bool>? _realTrafficOnAir;

    /// <summary>Un appareil est-il en détresse ? Le bavardage se tait alors complètement.</summary>
    public Func<bool>? DistressInProgress { get; set; }

    /// <summary>Annonce copilote émise (pour le journal, optionnel).</summary>
    public event Action<string>? CopilotSaid;

    /// <summary>Prise de parole d'ambiance émise (pour le journal).</summary>
    public event Action<ChatterTurn>? ChatterSaid;

    public ImmersionController(
        ISimConnectService sim, FlightContextProvider flight, ITtsEngine tts, VoiceBus voice,
        CabinAudioPlayer cabinPlayer, CabinSoundPackRepository packs, SettingsService settings,
        LanguageResolver language, VoicePicker picker,
        Func<bool>? realTrafficOnAir = null)
    {
        _realTrafficOnAir = realTrafficOnAir;
        _picker = picker;
        _sim = sim;
        _flight = flight;
        _tts = tts;
        _voice = voice;
        _cabinPlayer = cabinPlayer;
        _packs = packs;
        _settings = settings;
        _language = language;

        _chatter.PlayerCallsign = () => _flight.Current().Callsign;
    }

    public void Start()
    {
        _sim.ContextReceived += OnContext;
        _sim.StateChanged += OnState;
    }

    private void OnState(ConnectionState state, string? _)
    {
        if (state == ConnectionState.Connected) return;
        _copilot.Reset();
        _chatter.Reset();
        _lastCabinPhase = FlightPhase.Unknown;
    }

    private void OnContext(ContextSnapshot c)
    {
        // Delta de temps réel entre deux instantanés (≈ 1 s, mais on ne le suppose pas).
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastTickSeconds, 0, 10);
        _lastTickSeconds = now;

        // Hors cockpit (menu principal, carte du monde, chargement) : aucune immersion — ni
        // copilote, ni trafic ambiant, ni cabine. Rien ne parle tant qu'on n'est pas aux commandes.
        if (!c.InFlightSession) return;

        var s = _settings.Current;
        var phase = _flight.EffectivePhase;

        if (s.CopilotEnabled) RunCopilot(c, phase, s);
        if (s.ChatterEnabled) RunChatter(dt, s);
        if (CabinPacksAvailable && s.CabinEnabled) RunCabin(phase, s);
        else _lastCabinPhase = phase; // suit la phase même désactivé (pas de rattrapage au ré-activage)
    }

    // ------------------------------------------------------------------ copilote

    private void RunCopilot(ContextSnapshot c, FlightPhase phase, AppSettings s)
    {
        var state = new CopilotState(
            OnGround: c.OnGround,
            IasKnots: c.IasKnots,
            AglFeet: c.AltitudeAglFeet,
            MslFeet: c.AltitudeMslFeet,
            VerticalSpeedFpm: c.VerticalSpeedFpm,
            GroundSpeedKnots: c.GroundSpeedKnots,
            Phase: phase);

        // Les vitesses réglées (135/140/148 kt par défaut) sont celles d'un avion de ligne :
        // sur un avion léger qui décolle vers 55 kt, AUCUNE n'était jamais atteinte avant la
        // rotation — le copilote restait donc muet de bout en bout. On bascule sur un jeu
        // adapté, et on désactive V1/V2 qui n'existent pas dans ce monde-là.
        //
        // Le RÉGLAGE l'emporte sur la déduction : voler en VFR sur un airbus pour s'entraîner,
        // ou vouloir des callouts de ligne sur un turbopropulseur, sont des demandes légitimes
        // que le gabarit de l'appareil ne peut pas deviner.
        bool light = s.CopilotRules switch
        {
            CopilotRulesMode.ForceVfr => true,
            CopilotRulesMode.ForceIfr => false,
            _ => _flight.RulesDecision.Class == AircraftClass.Light,
        };
        bool gearRetractable = _flight.Aircraft?.GearRetractable ?? true;

        var cfg = light
            ? new CopilotConfig(0, LightRotateKnots, 0, s.CopilotChecklists,
                                Light: true, GearRetractable: gearRetractable)
            : new CopilotConfig(s.CopilotV1Knots, s.CopilotVrKnots, s.CopilotV2Knots, s.CopilotChecklists,
                                Light: false, GearRetractable: gearRetractable);

        foreach (var key in _copilot.Update(state, cfg))
        {
            string? text = CopilotPhrases.Text(key);
            if (string.IsNullOrWhiteSpace(text)) continue;
            CopilotSaid?.Invoke(text!);
            // Voix choisie explicitement, sinon une voix anglaise.
            var voice = string.IsNullOrWhiteSpace(s.CopilotVoiceName)
                ? _picker.For("copilot", AtcLanguage.English)
                : new TtsVoice(s.CopilotVoiceName);
            // Annonces courtes et TEMPORELLES : on attend un peu le canal, sans le monopoliser.
            _ = SpeakAsync(text!, voice, DryProfile(), TimeSpan.FromSeconds(3));
        }
    }

    // ------------------------------------------------------------------ trafic ambiant

    private void RunChatter(double dt, AppSettings s)
    {
        // Jamais par-dessus une vraie transmission ATC.
        if (_voice.IsBusy) return;

        // NI PENDANT UNE DÉTRESSE. Le contrôleur a dégagé la fréquence pour l'appareil en
        // difficulté : des équipages inventés qui continuent à bavarder par-dessus seraient
        // la faute de goût la plus visible du logiciel.
        if (DistressInProgress?.Invoke() == true) return;

        // NI PAR-DESSUS LE TRAFIC RÉEL. Les échanges d'ambiance sont inventés : indicatifs,
        // demandes, tout. Tant qu'il y a de vrais appareils autour à faire parler, les
        // entendre serait pire qu'un silence — un « Vueling, request pushback » alors qu'aucun
        // Vueling n'est garé nulle part apprend au pilote à ne plus croire sa radio.
        if (_realTrafficOnAir?.Invoke() == true) return;

        var ctx = _flight.Current();
        // Les demandes correspondent au TYPE DE FRÉQUENCE écoutée (pas de repoussage
        // sur une fréquence de départ). Tout est en anglais.
        // Compagnies RÉGIONALES : au-dessus de Manille on entend des transporteurs asiatiques,
        // pas « Lufthansa 123 ». La région se déduit du préfixe OACI du terrain le plus proche.
        var region = AirlineRegistry.FromIcao(ctx.AirportIcao);
        var exchange = _chatter.Update(dt, s.ChatterMinGapSeconds, s.ChatterMaxGapSeconds,
                                       ctx.StationName,
                                       ScopeFor(ctx.Controller, ctx.Phase, ctx.OnGround),
                                       ctx.Rules, region);
        if (exchange is null) return;

        _ = SpeakExchangeAsync(exchange, s);
    }

    /// <summary>
    /// Joue un échange complet : chaque interlocuteur a SA voix (l'équipage d'après son
    /// indicatif, le contrôle d'après la station), avec une petite respiration entre les deux.
    /// </summary>
    private async Task SpeakExchangeAsync(ChatterExchange exchange, AppSettings s)
    {
        var profile = ChatterProfile(s);
        const AtcLanguage lang = AtcLanguage.English;   // voix anglaise pour du texte anglais
        bool first = true;

        foreach (var turn in exchange.Turns)
        {
            // 1re prise de parole : on abandonne si le canal est occupé. Les suivantes
            // attendent un peu pour ne pas couper l'échange en deux.
            var wait = first ? TimeSpan.Zero : TimeSpan.FromSeconds(6);
            first = false;

            ChatterSaid?.Invoke(turn);
            bool spoken = await SpeakAsync(turn.Text, _picker.For(turn.Speaker, lang), profile, wait, VoicePriority.Ambient)
                              .ConfigureAwait(false);
            if (!spoken) return; // canal pris par le vrai ATC -> on laisse tomber la suite

            await Task.Delay(_rng.Next(400, 1100)).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ cabine

    private void RunCabin(FlightPhase phase, AppSettings s)
    {
        if (phase == _lastCabinPhase) return;
        var previous = _lastCabinPhase;
        _lastCabinPhase = phase;

        string? evt = phase switch
        {
            FlightPhase.Parked => previous is FlightPhase.TaxiIn or FlightPhase.Landing ? "deboarding" : "boarding",
            FlightPhase.Pushback => "safety",
            FlightPhase.Takeoff => "takeoff",
            FlightPhase.Airborne => "cruise",
            FlightPhase.Approach => "descent",
            FlightPhase.Landing => "landing",
            FlightPhase.TaxiIn => "deboarding",
            _ => null,
        };
        if (evt is null) return;

        var pack = _packs.Resolve(s.CabinPackName);
        string? file = pack?.FileFor(evt);
        if (file is not null) _cabinPlayer.Play(file, s.OutputDeviceNumber, s.CabinVolume);
    }

    // ------------------------------------------------------------------ utilitaires

    /// <summary>
    /// Répertoire d'échanges cohérent avec la fréquence écoutée : on n'entend pas une
    /// demande de repoussage sur la fréquence de départ. Si le type de contrôleur est
    /// inconnu (fréquence absente des données), on déduit du contexte de vol.
    /// </summary>
    private static ChatterScope ScopeFor(ControllerType controller, FlightPhase phase, bool onGround)
        => controller switch
        {
            ControllerType.Ground or ControllerType.Clearance => ChatterScope.Ground,
            ControllerType.Tower => ChatterScope.Tower,
            ControllerType.Departure => ChatterScope.Departure,
            ControllerType.Approach => ChatterScope.Approach,
            ControllerType.Center => ChatterScope.Center,
            _ => phase switch                       // fréquence inconnue -> d'après la phase
            {
                FlightPhase.Parked or FlightPhase.Pushback or FlightPhase.TaxiOut
                    or FlightPhase.TaxiIn => ChatterScope.Ground,
                FlightPhase.Takeoff or FlightPhase.Landing => ChatterScope.Tower,
                FlightPhase.Approach => ChatterScope.Approach,
                _ => onGround ? ChatterScope.Ground : ChatterScope.Center,
            },
        };

    /// <summary>Voix d'interphone : aucun effet radio (le copilote est à côté de vous).</summary>
    private RadioProfile DryProfile() => new()
    {
        BandPass = false,
        Squelch = false,
        Saturation = false,
        Volume = _settings.Current.RadioVolume,
    };

    /// <summary>Ambiance : effet radio complet, un peu en retrait.</summary>
    private static RadioProfile ChatterProfile(AppSettings s)
    {
        var p = s.ToRadioProfile();
        p.Volume = Math.Clamp(p.Volume * 0.7, 0, 1);
        return p;
    }

    /// <param name="priority">
    /// Copilote ou ambiance : dans les deux cas, une réponse du contrôleur passe devant et
    /// coupe l'annonce en cours. Un « positive rate » n'a pas à retarder une clairance.
    /// </param>
    private async Task<bool> SpeakAsync(string text, TtsVoice voice, RadioProfile profile, TimeSpan wait,
                                        VoicePriority priority = VoicePriority.Copilot)
    {
        try
        {
            TtsAudio audio = await _tts.SynthesizeAsync(text, voice).ConfigureAwait(false);
            return await _voice.SpeakAsync(audio, profile, wait, priority: priority).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[WilcoATC/Immersion] " + ex);
            return false;
        }
    }

    public void Dispose()
    {
        _sim.ContextReceived -= OnContext;
        _sim.StateChanged -= OnState;
        _cabinPlayer.Dispose();
    }
}
