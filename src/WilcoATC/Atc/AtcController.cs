using FreqWatch.Atc.Brain;
using FreqWatch.Atc.Context;
using FreqWatch.Atc.GroundServices;
using FreqWatch.Atc.Planning;
using FreqWatch.Atc.Understanding;
using FreqWatch.Atc.Vatsim;
using FreqWatch.Audio;
using FreqWatch.Common;
using FreqWatch.Formatting;
using FreqWatch.Localization;
using FreqWatch.Settings;
using FreqWatch.Sim;
using FreqWatch.Stations;

namespace FreqWatch.Atc;

/// <summary>
/// Cœur de la boucle vocale. S'abonne à la couche SimConnect (SANS la modifier),
/// agrège un <see cref="FlightSnapshot"/>, et déclenche des transmissions :
///  - AUTO : quand le joueur se cale sur la fréquence d'une station connue (résolue
///    via <see cref="IStationResolver"/>) — un contact initial, une seule fois par station ;
///  - MANUEL : bouton / touche de test.
///
/// Une seule transmission à la fois (SemaphoreSlim). Un petit délai aléatoire évite
/// l'effet « réponse instantanée robotique ». Rien ne bloque l'UI (tout est async).
/// </summary>
public sealed class AtcController : IAtcController
{
    private readonly ISimConnectService _sim;
    private readonly IStationResolver _stations;
    private readonly IAtcLineGenerator _generator;
    private readonly ITtsEngine _tts;
    private readonly RadioAudioPipeline _pipeline;
    private readonly SettingsService _settings;
    private readonly IIntentRecognizer _intents;
    private readonly AtcBrain _brain;
    private readonly FlightContextProvider _flightContext;
    private readonly CallsignFormatter _callsigns;
    private readonly IGroundServices _groundServices;
    private readonly FlightPlanStore _plans;
    private readonly ControllerSequencer _sequencer = new();
    private readonly VatsimClient _vatsim = new();

    private readonly Random _rng = new();
    private readonly SemaphoreSlim _speaking = new(1, 1);

    private RadioSnapshot? _radio;
    private ContextSnapshot? _context;
    private AircraftSnapshot? _aircraft;
    private string? _lastStationKey;

    // ATC proactif : suivi des transitions de phase + état d'autorisation de décollage.
    private FlightPhase _lastDirectorPhase = FlightPhase.Unknown;
    private bool _takeoffCleared;
    private bool _forceCleared; // override DEBUG (Mode Test) : traite le décollage comme autorisé
    private readonly HashSet<string> _announced = new();

    public event Action<string>? TransmissionText;
    public event Action<string>? StatusChanged;
    public event Action<string>? PilotTranscript;
    public event Action<RecognizedIntent>? IntentRecognized;
    public event Action<AtcDecision>? DecisionMade;
    public event Action<FlightPhaseDebug>? PhaseChanged;
    public event Action<bool>? ExpectingReadbackChanged;

    // Collationnement : mots que l'ATC vient de dire, requêtes déjà accordées (par vol).
    private readonly object _stateLock = new();
    private bool _expectingReadback;
    private string _lastAtcWords = "";
    private readonly HashSet<PilotIntent> _granted = new();

    public bool Enabled
    {
        get => _settings.Current.AtcEnabled;
        set { _settings.Current.AtcEnabled = value; _settings.Save(); }
    }

    public bool TestMode
    {
        get => _settings.Current.TestMode;
        set { _settings.Current.TestMode = value; _settings.Save(); }
    }

    public AtcController(
        ISimConnectService sim, IStationResolver stations, IAtcLineGenerator generator,
        ITtsEngine tts, RadioAudioPipeline pipeline, SettingsService settings,
        IIntentRecognizer intents, AtcBrain brain, FlightContextProvider flightContext,
        CallsignFormatter callsigns, IGroundServices groundServices, FlightPlanStore plans)
    {
        _sim = sim;
        _stations = stations;
        _generator = generator;
        _tts = tts;
        _pipeline = pipeline;
        _settings = settings;
        _intents = intents;
        _brain = brain;
        _flightContext = flightContext;
        _callsigns = callsigns;
        _groundServices = groundServices;
        _plans = plans;
    }

    public void Start()
    {
        _sim.RadioSnapshotReceived += OnRadio;
        _sim.ContextReceived += OnContext;
        _sim.AircraftReceived += OnAircraft;
        _sim.StateChanged += OnState;
        SetStatus("Prêt");
    }

    // ------------------------------------------------------------------ événements sim

    private void OnState(ConnectionState state, string? detail)
    {
        if (state != ConnectionState.Connected)
        {
            _lastStationKey = null;      // ré-armer après reconnexion
            _flightContext.Reset();
            _lastDirectorPhase = FlightPhase.Unknown;
            _takeoffCleared = false;
            _announced.Clear();
            _sequencer.Reset();
            ResetReadbackState();
        }
    }

    private void OnRadio(RadioSnapshot r) { _radio = r; _flightContext.OnRadio(r); CheckAutoContact(); }

    private void OnContext(ContextSnapshot c)
    {
        _context = c;
        _flightContext.OnContext(c);

        var phase = _flightContext.EffectivePhase;
        PhaseChanged?.Invoke(new FlightPhaseDebug(phase, _flightContext.HasBeenAirborne, c.OnGround));
        RunDirector(phase);
        RunSequencer(c);
        CheckAutoContact();
    }

    // ------------------------------------------------------------------ transferts de fréquence

    private void RunSequencer(ContextSnapshot c)
    {
        if (!_settings.Current.AtcEnabled) return;

        var plan = _plans.Current;
        double distArrNm = double.MaxValue;
        if (plan is not null && plan.DestinationLat != 0 && plan.DestinationLon != 0)
            distArrNm = Geo.DistanceMeters(c.Latitude, c.Longitude, plan.DestinationLat, plan.DestinationLon) / 1852.0;

        var state = new FlightState(c.OnGround, c.AltitudeAglFeet, c.AltitudeMslFeet,
                                    c.VerticalSpeedFpm, c.GroundSpeedKnots, distArrNm);

        if (_sequencer.Update(state) is { } pos) _ = AnnounceTransferAsync(pos);
    }

    private async Task AnnounceTransferAsync(ControllerPosition pos)
    {
        try
        {
            var ctx = _flightContext.Current();
            var (name, freqHz) = await ResolveControllerAsync(pos, _plans.Current, ctx);

            // Transfert Centre sans fréquence fiable (VATSIM hors-ligne, pas de config) :
            // on ne joue pas un transfert bidon — le pilote reste sur la fréquence courante.
            if (pos == ControllerPosition.Center && freqHz <= 0)
            {
                System.Diagnostics.Debug.WriteLine("[FreqWatch/Atc] transfert Centre ignoré (aucune fréquence fiable).");
                return;
            }

            string text = _brain.AnnounceTransfer(ctx, name, freqHz);
            await SpeakRawAsync(text);
            SetExpectingReadback(true, text); // le pilote collationne le transfert
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[FreqWatch/Atc] transfert : " + ex); }
    }

    private async Task<(string Name, double FreqHz)> ResolveControllerAsync(ControllerPosition pos, FlightPlan? plan, FlightContext ctx)
    {
        // Sans plan de vol (pas de SimBrief), on retombe sur l'aéroport CONTRÔLÉ le plus
        // proche (celui qui a une fréquence Tour) plutôt que le « plus proche » brut de
        // SimConnect : ça évite un petit terrain voisin sans fréquence (ex. Melsbroek/EBMB
        // au lieu de Bruxelles/EBBR). Dernier repli : l'ICAO SimConnect.
        string? nearest = _stations.NearestControlledAirportIcao(_context?.Latitude ?? 0, _context?.Longitude ?? 0);
        string? depIcao = plan?.OriginIcao ?? nearest ?? ctx.AirportIcao;
        string? arrIcao = plan?.DestinationIcao ?? nearest ?? ctx.AirportIcao;

        return pos switch
        {
            ControllerPosition.Departure =>
                (TerminalName(depIcao, "Departure", "Départ"), FreqFor(depIcao, ControllerType.Departure, ControllerType.Approach)),
            ControllerPosition.Center =>
                (CenterName(), await CenterFrequencyHzAsync(depIcao ?? arrIcao)),
            ControllerPosition.Approach =>
                (TerminalName(arrIcao, "Approach", "Approche"), FreqFor(arrIcao, ControllerType.Approach, ControllerType.Departure)),
            ControllerPosition.ArrivalTower =>
                (TerminalName(arrIcao, "Tower", "Tour"), FreqFor(arrIcao, ControllerType.Tower)),
            ControllerPosition.ArrivalGround =>
                (TerminalName(arrIcao, "Ground", "Sol"), FreqFor(arrIcao, ControllerType.Ground)),
            _ => (pos.ToString(), 0),
        };
    }

    private double FreqFor(string? icao, params ControllerType[] types)
    {
        if (string.IsNullOrWhiteSpace(icao)) return 0;
        foreach (var t in types)
        {
            var hz = _stations.FindFrequencyHz(icao!, t);
            if (hz is not null) return hz.Value;
        }
        return 0;
    }

    private string TerminalName(string? icao, string enWord, string frWord)
    {
        string word = _brain.EffectiveLanguage == AtcLanguage.French ? frWord : enWord;
        string? name = icao is not null ? FlightPlan.CleanAirportName(_stations.LookupAirportName(icao)) : null;
        return string.IsNullOrWhiteSpace(name) ? word : $"{name} {word}";
    }

    private string CenterName()
    {
        string word = _brain.EffectiveLanguage == AtcLanguage.French ? "Centre" : "Center";
        string cfg = _settings.Current.CenterName;
        return string.IsNullOrWhiteSpace(cfg) ? word : $"{cfg} {word}";
    }

    // Centre : vraie fréquence VATSIM si en ligne. Sinon on N'INVENTE PAS une fréquence
    // bidon : l'approximative n'est utilisée QUE si l'utilisateur a explicitement configuré
    // un Centre (nom renseigné). Faute de quoi -> 0 (le transfert Centre sera ignoré).
    private async Task<double> CenterFrequencyHzAsync(string? icaoForRegion)
    {
        if (_settings.Current.VatsimEnabled && !string.IsNullOrWhiteSpace(icaoForRegion))
        {
            string prefix = icaoForRegion!.Length >= 2 ? icaoForRegion[..2] : icaoForRegion!;
            double? vhz = await _vatsim.FindCenterFrequencyHzAsync(prefix);
            if (vhz is not null) return vhz.Value;
        }

        if (!string.IsNullOrWhiteSpace(_settings.Current.CenterName))
        {
            System.Diagnostics.Debug.WriteLine("[FreqWatch/Atc] fréquence Centre APPROXIMATIVE (configurée).");
            return _settings.Current.CenterFrequencyMhz * 1_000_000.0;
        }

        System.Diagnostics.Debug.WriteLine("[FreqWatch/Atc] pas de fréquence Centre fiable -> transfert Centre ignoré.");
        return 0;
    }

    // ATC proactif : sur chaque transition de phase, l'ATC peut initier une transmission
    // (décollage sans clairance, transfert de fréquence, approche, atterrissage…).
    private void RunDirector(FlightPhase phase)
    {
        if (phase == _lastDirectorPhase) return;
        var prev = _lastDirectorPhase;
        _lastDirectorPhase = phase;

        if (phase == FlightPhase.Parked) // nouveau vol : on réarme
        {
            _takeoffCleared = false;
            _announced.Clear();
            _sequencer.Reset();
            ResetReadbackState();
        }

        if (!_settings.Current.AtcEnabled) return;

        // En Mode Test (ou override debug « cleared »), on considère le décollage comme
        // autorisé : l'avertissement « décollé sans autorisation » n'est PAS déclenché.
        bool cleared = _takeoffCleared || _forceCleared || _settings.Current.TestMode;

        string? key = FlightDirector.OnPhaseTransition(prev, phase, cleared);
        if (key is null || !_announced.Add(key)) return; // une seule fois par vol

        string? text = _brain.Announce(key, _flightContext.Current());
        if (!string.IsNullOrWhiteSpace(text))
        {
            SetExpectingReadback(true, text!); // l'ATC vient d'instruire -> on attend un collationnement
            _ = SpeakRawAsync(text!);
        }
    }

    private void ResetReadbackState()
    {
        lock (_stateLock) { _granted.Clear(); _expectingReadback = false; _lastAtcWords = ""; }
        ExpectingReadbackChanged?.Invoke(false);
    }

    private void OnAircraft(AircraftSnapshot a) { _aircraft = a; _flightContext.OnAircraft(a); }

    // ------------------------------------------------------------------ déclencheur auto

    private void CheckAutoContact()
    {
        if (!_settings.Current.AtcEnabled || !_settings.Current.AtcAutoContact) return;

        var r = _radio; var c = _context;
        if (r is null || c is null) return;

        // Réalisme COM : on ne parle que si la radio est calée sur une station connue.
        string? station = _stations.Resolve(r.Com1ActiveHz, c.Latitude, c.Longitude);
        if (station is null) return;

        string key = $"{Math.Round(r.Com1ActiveHz / 1000.0)}|{station}";
        if (key == _lastStationKey) return; // déjà contactée sur cette fréquence
        _lastStationKey = key;

        _ = SpeakAsync(AtcTrigger.InitialContact);
    }

    public void TriggerManualTest() => _ = SpeakAsync(AtcTrigger.ManualTest);

    // ------------------------------------------------------------------ requête pilote

    public void SetControllerOverride(ControllerType? controller) => _flightContext.SetControllerOverride(controller);

    // ------------------------------------------------------------------ overrides DEBUG (Mode Test)

    public void SetPhaseOverride(FlightPhase? phase)
    {
        // Force l'état de phase utilisé pour VALIDER les requêtes (Current().Phase). On ne
        // touche pas au suivi du director proactif (évite un ré-déclenchement parasite).
        _flightContext.SetPhaseOverride(phase);
        EmitPhaseDebug();
    }

    public void SetHasBeenAirborneOverride(bool? value)
    {
        _flightContext.SetHasBeenAirborneOverride(value);
        EmitPhaseDebug();
    }

    public void SetTakeoffClearedOverride(bool cleared)
    {
        _forceCleared = cleared;
        if (cleared) _takeoffCleared = true;
    }

    // Ré-émet l'état de phase pour rafraîchir l'UI immédiatement (utile hors simulateur).
    private void EmitPhaseDebug()
        => PhaseChanged?.Invoke(new FlightPhaseDebug(
               _flightContext.EffectivePhase, _flightContext.HasBeenAirborne, _context?.OnGround ?? true));

    public void HandlePilotText(string text) => _ = HandlePilotAsync(text);

    private async Task HandlePilotAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        PilotTranscript?.Invoke(text.Trim());

        RecognizedIntent recognized;
        try { recognized = await _intents.RecognizeAsync(text); }
        catch { recognized = new RecognizedIntent(PilotIntent.Unknown, text, "error"); }

        // État courant du sous-état collationnement (lu sous verrou).
        bool expecting; string atcWords; bool alreadyGranted;
        lock (_stateLock)
        {
            expecting = _expectingReadback;
            atcWords = _lastAtcWords;
            alreadyGranted = IsClearanceRequest(recognized.Intent) && _granted.Contains(recognized.Intent);
        }

        string callsign = _callsigns.Speak(_aircraft?.TailNumber);

        // (6) PRIORITÉ COLLATIONNEMENT : en attente de readback, une répétition/accusé/callsign
        //     l'emporte sur toute détection de requête -> on ne valide PAS comme une requête.
        if (expecting && ReadbackDetector.IsReadback(text, callsign, atcWords))
            recognized = recognized with { Intent = PilotIntent.Readback, Source = "readback-context", Reason = "collationnement (contexte)" };

        IntentRecognized?.Invoke(recognized);

        FlightContext ctx = _flightContext.Current();
        AtcDecision decision;

        if (recognized.Intent == PilotIntent.Readback)
        {
            // (4) Un readback n'est JAMAIS refusé pour cause de phase.
            SetExpectingReadback(false);
            decision = _brain.Evaluate(recognized, ctx); // règle READBACK -> "collationnement correct"
        }
        else if (alreadyGranted)
        {
            // (5) Requête déjà accordée -> "déjà approuvé" au lieu d'un refus de phase.
            decision = new AtcDecision(true, recognized.Intent, _brain.AlreadyApproved(ctx), "déjà accordé", null);
        }
        else
        {
            decision = _brain.Evaluate(recognized, ctx);

            if (decision.Approved && decision.AdvanceTo == FlightPhase.Pushback)
            {
                _flightContext.MarkPushbackGranted();
                _groundServices.RequestPushback(); // -> GSX (si activé)
            }
            if (decision.Approved && recognized.Intent == PilotIntent.ReadyForDeparture)
                _takeoffCleared = true;

            // (2) Après une clairance/approbation, on attend un collationnement.
            if (decision.Approved && IsClearanceRequest(recognized.Intent))
            {
                lock (_stateLock) _granted.Add(recognized.Intent);
                SetExpectingReadback(true, decision.ResponseText);
            }
        }

        DecisionMade?.Invoke(decision);
        await SpeakRawAsync(decision.ResponseText);
    }

    private static bool IsClearanceRequest(PilotIntent i) => i is
        PilotIntent.RequestClearance or PilotIntent.RequestPushback or
        PilotIntent.RequestTaxi or PilotIntent.ReadyForDeparture;

    private void SetExpectingReadback(bool value, string atcWords = "")
    {
        lock (_stateLock)
        {
            _expectingReadback = value;
            if (value) _lastAtcWords = atcWords;
        }
        ExpectingReadbackChanged?.Invoke(value);
    }

    // Émet un texte déjà rédigé (réponse du brain) sans passer par le générateur.
    // On MET EN FILE (au lieu de laisser tomber) : réponses pilote et transferts sont trop
    // importants pour être perdus si l'ATC est déjà en train de parler. Garde-fou : si le
    // canal reste bloqué > 30 s, on abandonne cette transmission plutôt que de figer.
    private async Task SpeakRawAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!await _speaking.WaitAsync(TimeSpan.FromSeconds(30))) return;
        try
        {
            SetStatus(Loc.T("S.Status.Transmitting"));
            await Task.Delay(_rng.Next(300, 900)); // court délai réaliste
            TransmissionText?.Invoke(text);
            TtsAudio audio = await _tts.SynthesizeAsync(text);
            await _pipeline.PlayAsync(audio, _settings.Current.OutputDeviceNumber, _settings.Current.ToRadioProfile());
            SetStatus(Loc.T("S.Status.Ready"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("S.Status.AudioError"));
            System.Diagnostics.Debug.WriteLine("[FreqWatch/Atc] " + ex);
        }
        finally { _speaking.Release(); }
    }

    // ------------------------------------------------------------------ émission

    private async Task SpeakAsync(AtcTrigger trigger)
    {
        // L'auto respecte l'interrupteur ATC ; le test manuel marche toujours.
        if (trigger == AtcTrigger.InitialContact && !_settings.Current.AtcEnabled) return;

        if (!_speaking.Wait(0)) return; // déjà en train de parler -> on ignore
        try
        {
            SetStatus(Loc.T("S.Status.Transmitting"));
            var flight = BuildSnapshot();

            // Délai réaliste avant de « répondre ».
            int delay = trigger == AtcTrigger.ManualTest ? _rng.Next(150, 500) : _rng.Next(1500, 4000);
            await Task.Delay(delay);

            string text = await _generator.GenerateAsync(flight, trigger);
            if (string.IsNullOrWhiteSpace(text)) { SetStatus("Prêt"); return; }

            TransmissionText?.Invoke(text);

            TtsAudio audio = await _tts.SynthesizeAsync(text);
            await _pipeline.PlayAsync(audio, _settings.Current.OutputDeviceNumber, _settings.Current.ToRadioProfile());

            SetStatus(Loc.T("S.Status.Ready"));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("S.Status.AudioError"));
            System.Diagnostics.Debug.WriteLine("[FreqWatch/Atc] " + ex);
        }
        finally
        {
            _speaking.Release();
        }
    }

    private FlightSnapshot BuildSnapshot()
    {
        var r = _radio; var c = _context; var a = _aircraft;

        // Indicatif parlé : plan de vol (SimBrief) en priorité, sinon immat phonétique.
        string callsign = _callsigns.Speak(a?.TailNumber);

        double hz = r?.Com1ActiveHz ?? 0;
        string? station = (r is not null && c is not null)
            ? _stations.Resolve(hz, c.Latitude, c.Longitude)
            : null;

        return new FlightSnapshot(
            Callsign: callsign,
            AircraftTitle: a?.Title ?? "",
            OnGround: c?.OnGround ?? true,
            AltitudeMslFeet: c?.AltitudeMslFeet ?? 0,
            AltitudeAglFeet: c?.AltitudeAglFeet ?? 0,
            HeadingTrueDeg: c?.HeadingTrueDeg ?? 0,
            IasKnots: c?.IasKnots ?? 0,
            GroundSpeedKnots: c?.GroundSpeedKnots ?? 0,
            Com1ActiveMhz: FrequencyFormatter.FormatMHz(hz),
            Com1ActiveHz: hz,
            Station: station,
            NearestAirportIcao: c?.NearestAirportIcao,
            Latitude: c?.Latitude ?? 0,
            Longitude: c?.Longitude ?? 0);
    }

    private void SetStatus(string s) => StatusChanged?.Invoke(s);

    public void Dispose()
    {
        _sim.RadioSnapshotReceived -= OnRadio;
        _sim.ContextReceived -= OnContext;
        _sim.AircraftReceived -= OnAircraft;
        _sim.StateChanged -= OnState;
        _pipeline.Stop();
        _speaking.Dispose();
    }
}
