using System.Diagnostics;
using FreqWatch.Atc;
using FreqWatch.Atc.Context;
using FreqWatch.Audio;
using FreqWatch.Common;
using FreqWatch.Settings;
using FreqWatch.Sim;

namespace FreqWatch.Immersion;

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

    private double _lastTickSeconds;
    private FlightPhase _lastCabinPhase = FlightPhase.Unknown;

    /// <summary>Annonce copilote émise (pour le journal, optionnel).</summary>
    public event Action<string>? CopilotSaid;

    /// <summary>Prise de parole d'ambiance émise (pour le journal).</summary>
    public event Action<ChatterTurn>? ChatterSaid;

    public ImmersionController(
        ISimConnectService sim, FlightContextProvider flight, ITtsEngine tts, VoiceBus voice,
        CabinAudioPlayer cabinPlayer, CabinSoundPackRepository packs, SettingsService settings,
        LanguageResolver language, VoicePicker picker)
    {
        _picker = picker;
        _sim = sim;
        _flight = flight;
        _tts = tts;
        _voice = voice;
        _cabinPlayer = cabinPlayer;
        _packs = packs;
        _settings = settings;
        _language = language;
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

        var cfg = new CopilotConfig(s.CopilotV1Knots, s.CopilotVrKnots, s.CopilotV2Knots, s.CopilotChecklists);

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

        var ctx = _flight.Current();
        // Les demandes correspondent au TYPE DE FRÉQUENCE écoutée (pas de repoussage
        // sur une fréquence de départ). Tout est en anglais.
        var exchange = _chatter.Update(dt, s.ChatterMinGapSeconds, s.ChatterMaxGapSeconds,
                                       ctx.StationName,
                                       ScopeFor(ctx.Controller, ctx.Phase, ctx.OnGround));
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
            bool spoken = await SpeakAsync(turn.Text, _picker.For(turn.Speaker, lang), profile, wait)
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
        Hiss = false,
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

    private async Task<bool> SpeakAsync(string text, TtsVoice voice, RadioProfile profile, TimeSpan wait)
    {
        try
        {
            TtsAudio audio = await _tts.SynthesizeAsync(text, voice).ConfigureAwait(false);
            return await _voice.SpeakAsync(audio, profile, wait).ConfigureAwait(false);
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
