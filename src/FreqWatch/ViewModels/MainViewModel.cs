using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using FreqWatch.Atc;
using FreqWatch.Atc.Brain;
using FreqWatch.Atc.Context;
using FreqWatch.Atc.Planning;
using FreqWatch.Atc.Understanding;
using FreqWatch.Common;
using FreqWatch.Formatting;
using FreqWatch.Localization;
using FreqWatch.Sim;
using FreqWatch.Stations;

namespace FreqWatch.ViewModels;

/// <summary>
/// ViewModel principal. Il s'abonne aux événements de la couche SimConnect (levés
/// sur le thread de pompage) et marshalle vers le thread UI via le Dispatcher.
/// Toutes les propriétés/collections observables ne sont touchées que sur l'UI.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly ISimConnectService _sim;
    private readonly IStationResolver _stations;
    private readonly IAtcController _atc;
    private readonly ISpeechToText _stt;
    private readonly Dispatcher _dispatcher;

    private const int MaxLogEntries = 500;

    public ComRadioViewModel Com1 { get; } = new("COM 1");
    public ComRadioViewModel Com2 { get; } = new("COM 2");

    /// <summary>Journal, le plus récent en tête.</summary>
    public ObservableCollection<LogEntryViewModel> Log { get; } = new();

    // Commandes ATC.
    public ICommand TestTransmissionCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    /// <summary>Levé quand l'utilisateur demande les réglages (l'ouverture est gérée côté fenêtre).</summary>
    public event Action? OpenSettingsRequested;

    public MainViewModel(ISimConnectService sim, IStationResolver stations, IAtcController atc,
                         ISpeechToText stt, FlightPlanStore plans, Dispatcher dispatcher)
    {
        _sim = sim;
        _stations = stations;
        _atc = atc;
        _stt = stt;
        _dispatcher = dispatcher;

        plans.Changed += OnFlightPlanChanged;
        OnFlightPlanChanged(plans.Current);

        _sim.StateChanged += OnStateChanged;
        _sim.RadioSnapshotReceived += OnRadioSnapshot;
        _sim.RadioChanged += OnRadioChanged;
        _sim.ContextReceived += OnContext;
        _sim.AircraftReceived += OnAircraft;

        _atc.TransmissionText += OnAtcTransmission;
        _atc.StatusChanged += OnAtcStatus;
        _atc.PilotTranscript += OnPilotTranscript;
        _atc.IntentRecognized += OnIntentRecognized;
        _atc.DecisionMade += OnDecisionMade;
        _atc.PhaseChanged += OnPhaseChanged;
        _atc.ExpectingReadbackChanged += OnExpectingReadbackChanged;
        _atcEnabled = _atc.Enabled;
        _testModeEnabled = _atc.TestMode;

        TestTransmissionCommand = new RelayCommand(() => _atc.TriggerManualTest());
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());
        SendPilotRequestCommand = new RelayCommand(SendPilotRequest);
        ToggleListenCommand = new RelayCommand(() => _ = ToggleListenAsync());

        AddSystemLog("WilcoATC started — waiting for the simulator…");
    }

    // ------------------------------------------------------------------ état de connexion

    private ConnectionState _state = ConnectionState.Waiting;
    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            Raise(nameof(IsConnected));
            Raise(nameof(IsWaiting));
            Raise(nameof(IsMissingDependency));
        }
    }

    public bool IsConnected => _state == ConnectionState.Connected;
    public bool IsWaiting => _state == ConnectionState.Waiting;
    public bool IsMissingDependency => _state == ConnectionState.MissingDependency;

    private string _statusText = Loc.T("S.Waiting");
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    // ------------------------------------------------------------------ ATC (voix)

    private bool _atcEnabled;
    public bool AtcEnabled
    {
        get => _atcEnabled;
        set { if (SetProperty(ref _atcEnabled, value)) _atc.Enabled = value; }
    }

    private string _atcStatus = Loc.T("S.Atc.StatusDash");
    public string AtcStatus { get => _atcStatus; private set => SetProperty(ref _atcStatus, value); }

    private void OnAtcTransmission(string text) => OnUi(() => AddLog(text, LogKind.Atc));
    private void OnAtcStatus(string status) => OnUi(() => AtcStatus = Loc.T("S.Atc.StatusPrefix") + status);

    // ------------------------------------------------------------------ Mode Test (débogage)

    private bool _testModeEnabled;
    /// <summary>Mode Test : le cerveau accepte toute requête (court-circuit de validation).</summary>
    public bool TestModeEnabled
    {
        get => _testModeEnabled;
        set { if (SetProperty(ref _testModeEnabled, value)) _atc.TestMode = value; }
    }

    /// <summary>Ré-aligne les toggles persistés (ATC / Mode Test) après fermeture des réglages.</summary>
    public void RefreshFromSettings()
    {
        if (_atcEnabled != _atc.Enabled) { _atcEnabled = _atc.Enabled; Raise(nameof(AtcEnabled)); }
        if (_testModeEnabled != _atc.TestMode) { _testModeEnabled = _atc.TestMode; Raise(nameof(TestModeEnabled)); }
    }

    // Le sélecteur de PHASE forcée a été retiré : aucune requête n'est plus refusée à cause
    // de la phase de vol, il n'y a donc plus rien à contourner en débogage.

    private bool _forceClearedForTakeoff;
    /// <summary>Force le flag CLEARED_FOR_TAKEOFF (coché = autorisé au décollage).</summary>
    public bool ForceClearedForTakeoff
    {
        get => _forceClearedForTakeoff;
        set { if (SetProperty(ref _forceClearedForTakeoff, value)) _atc.SetTakeoffClearedOverride(value); }
    }

    // ------------------------------------------------------------------ plan de vol (SimBrief)

    private bool _hasFlightPlan;
    public bool HasFlightPlan { get => _hasFlightPlan; private set => SetProperty(ref _hasFlightPlan, value); }

    private string _flightPlanCallsign = "—";
    public string FlightPlanCallsign { get => _flightPlanCallsign; private set => SetProperty(ref _flightPlanCallsign, value); }

    private string _flightPlanRoute = "—";
    public string FlightPlanRoute { get => _flightPlanRoute; private set => SetProperty(ref _flightPlanRoute, value); }

    private string _flightPlanCruise = "—";
    public string FlightPlanCruise { get => _flightPlanCruise; private set => SetProperty(ref _flightPlanCruise, value); }

    private string _flightPlanAircraft = "—";
    public string FlightPlanAircraft { get => _flightPlanAircraft; private set => SetProperty(ref _flightPlanAircraft, value); }

    private void OnFlightPlanChanged(FlightPlan? p) => OnUi(() =>
    {
        HasFlightPlan = p is not null;
        if (p is null) return;

        FlightPlanCallsign = string.IsNullOrWhiteSpace(p.AtcCallsign)
            ? $"{p.AirlineIcao}{p.FlightNumber}" : p.AtcCallsign!;
        FlightPlanRoute = $"{p.OriginIcao} → {p.DestinationIcao}";
        FlightPlanCruise = FormatCruise(p.CruiseAltitudeFeet);
        FlightPlanAircraft = p.AircraftIcao ?? "—";
        AddSystemLog("Flight plan loaded: " + p.Summary);
    });

    private static string FormatCruise(int feet)
    {
        if (feet <= 0) return "—";
        return feet >= 18000 ? $"FL{feet / 100:000}" : $"{feet:N0} ft";
    }

    // ------------------------------------------------------------------ console pilote

    public ICommand SendPilotRequestCommand { get; }

    private string _pilotRequestText = "";
    public string PilotRequestText { get => _pilotRequestText; set => SetProperty(ref _pilotRequestText, value); }

    // ------------------------------------------------------------------ reconnaissance vocale (STT)

    public ICommand ToggleListenCommand { get; }

    /// <summary>Un moteur ASR est prêt (modèle Whisper installé) -> bouton micro actif.</summary>
    public bool SpeechAvailable => _stt.IsAvailable;

    private bool _isListening;
    public bool IsListening
    {
        get => _isListening;
        private set { if (SetProperty(ref _isListening, value)) Raise(nameof(ListenButtonText)); }
    }

    /// <summary>Libellé du bouton micro selon l'état (parler / transcrire).</summary>
    public string ListenButtonText => _isListening ? Loc.T("S.Btn.Transcribe") : Loc.T("S.Btn.Speak");

    /// <summary>Début de capture (push-to-talk : touche enfoncée). Idempotent.</summary>
    public void StartListening() => OnUi(() =>
    {
        if (_isListening || !_stt.IsAvailable) return;
        try { _stt.StartListening(); IsListening = true; AtcStatus = Loc.T("S.Atc.StatusPrefix") + Loc.T("S.Status.Listening"); }
        catch { IsListening = false; AtcStatus = Loc.T("S.Atc.StatusPrefix") + Loc.T("S.Status.MicUnavailable"); }
    });

    /// <summary>Fin de capture (push-to-talk : touche relâchée) -> transcription + envoi.</summary>
    public void StopListeningAndSend() => OnUi(() => { if (_isListening) _ = FinishListeningAsync(); });

    /// <summary>Ajoute une ligne au journal (annonces copilote / trafic ambiant).</summary>
    public void LogLine(string text, LogKind kind) => OnUi(() => AddLog(text, kind));

    private async Task ToggleListenAsync()
    {
        if (!_stt.IsAvailable) return;

        if (!_isListening)
        {
            StartListening();
            return;
        }

        await FinishListeningAsync();
    }

    private async Task FinishListeningAsync()
    {
        // On ferme le micro, on transcrit, et on injecte le texte comme une requête.
        IsListening = false;
        AtcStatus = Loc.T("S.Atc.StatusPrefix") + Loc.T("S.Status.Transcribing");
        string text = "";
        try { text = await _stt.StopAndTranscribeAsync(); }
        catch { /* transcription indisponible -> on ignore */ }

        if (!string.IsNullOrWhiteSpace(text)) _atc.HandlePilotText(text);
        else AtcStatus = Loc.T("S.Atc.StatusPrefix") + Loc.T("S.Status.NothingHeard");
    }

    private string _lastTranscript = "—";
    public string LastTranscript { get => _lastTranscript; private set => SetProperty(ref _lastTranscript, value); }

    private string _lastIntentText = "—";
    public string LastIntentText { get => _lastIntentText; private set => SetProperty(ref _lastIntentText, value); }

    private string _lastDecisionText = "—";
    public string LastDecisionText { get => _lastDecisionText; private set => SetProperty(ref _lastDecisionText, value); }

    private bool _lastDecisionApproved;
    public bool LastDecisionApproved { get => _lastDecisionApproved; private set => SetProperty(ref _lastDecisionApproved, value); }

    // Debug de la machine à états de phase (phase courante + HasBeenAirborne).
    private string _phaseDebugText = "—";
    public string PhaseDebugText { get => _phaseDebugText; private set => SetProperty(ref _phaseDebugText, value); }

    private void OnPhaseChanged(FlightPhaseDebug d) => OnUi(() =>
        PhaseDebugText = $"{d.Phase}  ·  airborne={(d.HasBeenAirborne ? "true" : "false")}  ·  {(d.OnGround ? Loc.T("S.Phase.Ground") : Loc.T("S.Phase.Air"))}");

    // Sous-état « en attente de collationnement » (debug).
    private string _readbackState = Loc.T("S.Readback.No");
    public string ReadbackState { get => _readbackState; private set => SetProperty(ref _readbackState, value); }

    private void OnExpectingReadbackChanged(bool expecting) => OnUi(() =>
        ReadbackState = Loc.T(expecting ? "S.Readback.Yes" : "S.Readback.No"));

    /// <summary>Options d'override du contrôleur (pour tester la validation sans les CSV).</summary>
    public IReadOnlyList<string> ControllerOverrides { get; } =
        new[] { "Auto", "Clearance", "Ground", "Tower", "Approach", "Departure", "Center" };

    private string _selectedControllerOverride = "Auto";
    public string SelectedControllerOverride
    {
        get => _selectedControllerOverride;
        set
        {
            if (!SetProperty(ref _selectedControllerOverride, value)) return;
            ControllerType? ov = value == "Auto" || !Enum.TryParse<ControllerType>(value, out var t) ? null : t;
            _atc.SetControllerOverride(ov);
        }
    }

    private void SendPilotRequest()
    {
        string text = PilotRequestText;
        if (string.IsNullOrWhiteSpace(text)) return;
        _atc.HandlePilotText(text);
        PilotRequestText = "";
    }

    private void OnPilotTranscript(string text) => OnUi(() =>
    {
        LastTranscript = text;
        AddLog("PILOT: " + text, LogKind.Pilot);
    });

    private void OnIntentRecognized(RecognizedIntent r) => OnUi(() =>
    {
        // Toujours afficher l'intention ; en UNKNOWN, montrer la RAISON (ASR ? grammaire ?).
        LastIntentText = r.Intent == PilotIntent.Unknown && !string.IsNullOrWhiteSpace(r.Reason)
            ? $"Unknown — {r.Reason}"
            : $"{r.Intent}" + (string.IsNullOrWhiteSpace(r.Reason) ? "" : $"  ({r.Reason})");
    });

    private void OnDecisionMade(AtcDecision d) => OnUi(() =>
    {
        LastDecisionApproved = d.Approved;
        LastDecisionText = Loc.T(d.Approved ? "S.Decision.Granted" : "S.Decision.Denied") + " — " + d.DebugReason;
        // La réponse elle-même (voix) sera journalisée via TransmissionText ; ici on
        // marque juste le refus en rouge pour le débogage.
        if (!d.Approved) AddLog("ATC (denied): " + d.ResponseText, LogKind.Refused);
    });

    // ------------------------------------------------------------------ identité avion

    private string _aircraftTitle = "—";
    public string AircraftTitle { get => _aircraftTitle; private set => SetProperty(ref _aircraftTitle, value); }

    private string _aircraftType = "";
    public string AircraftType { get => _aircraftType; private set => SetProperty(ref _aircraftType, value); }

    private string _tailNumber = "";
    public string TailNumber { get => _tailNumber; private set => SetProperty(ref _tailNumber, value); }

    // ------------------------------------------------------------------ contexte de vol

    private string _ias = "—";
    public string Ias { get => _ias; private set => SetProperty(ref _ias, value); }

    private string _groundSpeed = "—";
    public string GroundSpeed { get => _groundSpeed; private set => SetProperty(ref _groundSpeed, value); }

    private string _verticalSpeed = "—";
    public string VerticalSpeed { get => _verticalSpeed; private set => SetProperty(ref _verticalSpeed, value); }

    private string _altitudeMsl = "—";
    public string AltitudeMsl { get => _altitudeMsl; private set => SetProperty(ref _altitudeMsl, value); }

    private string _altitudeAgl = "—";
    public string AltitudeAgl { get => _altitudeAgl; private set => SetProperty(ref _altitudeAgl, value); }

    private string _heading = "—";
    public string Heading { get => _heading; private set => SetProperty(ref _heading, value); }

    private string _groundState = "—";
    public string GroundState { get => _groundState; private set => SetProperty(ref _groundState, value); }

    private string _squawk = "—";
    public string Squawk { get => _squawk; private set => SetProperty(ref _squawk, value); }

    private string _latitude = "—";
    public string Latitude { get => _latitude; private set => SetProperty(ref _latitude, value); }

    private string _longitude = "—";
    public string Longitude { get => _longitude; private set => SetProperty(ref _longitude, value); }

    private string _nearestAirport = "—";
    public string NearestAirport { get => _nearestAirport; private set => SetProperty(ref _nearestAirport, value); }

    // ------------------------------------------------------------------ handlers (marshalés UI)

    private void OnStateChanged(ConnectionState state, string? detail) => OnUi(() =>
    {
        State = state;
        StatusText = detail ?? state.ToString();

        switch (state)
        {
            case ConnectionState.Waiting:
                Com1.Active = Com1.Standby = "---.---";
                Com2.Active = Com2.Standby = "---.---";
                Com1.IsTransmitting = Com2.IsTransmitting = false;
                Com1.Station = Com2.Station = null;
                ResetContext();
                AddSystemLog(detail ?? Loc.T("S.Waiting"));
                break;
            case ConnectionState.Connected:
                AddSystemLog(detail ?? "Connected to the simulator.");
                break;
            case ConnectionState.MissingDependency:
                AddSystemLog("Missing dependency: " + detail);
                break;
        }
    });

    private void OnRadioSnapshot(RadioSnapshot s) => OnUi(() =>
    {
        Com1.Active = FrequencyFormatter.FormatMHz(s.Com1ActiveHz);
        Com1.Standby = FrequencyFormatter.FormatMHz(s.Com1StandbyHz);
        Com2.Active = FrequencyFormatter.FormatMHz(s.Com2ActiveHz);
        Com2.Standby = FrequencyFormatter.FormatMHz(s.Com2StandbyHz);
        Com1.IsTransmitting = s.Com1Transmit;
        Com2.IsTransmitting = s.Com2Transmit;

        // Stretch : nom de station à partir de la fréquence active + dernière position connue.
        Com1.Station = _stations.Resolve(s.Com1ActiveHz, _lastLat, _lastLon);
        Com2.Station = _stations.Resolve(s.Com2ActiveHz, _lastLat, _lastLon);
    });

    private void OnRadioChanged(RadioChange c) => OnUi(() =>
    {
        string text;
        LogKind kind;

        if (c.Kind == RadioChangeKind.Transmit)
        {
            text = $"{c.RadioLabel} ÉMISSION {c.NewValue}";
            kind = LogKind.Transmit;
        }
        else
        {
            string arrow = c.Kind == RadioChangeKind.Initial ? "=" : "→";
            text = $"{c.RadioLabel} {c.FieldLabel} {arrow} {c.NewValue}";
            kind = c.Kind == RadioChangeKind.Initial ? LogKind.Initial : LogKind.Change;
        }
        AddLog(text, kind);
    });

    private double _lastLat, _lastLon;

    private void OnContext(ContextSnapshot c) => OnUi(() =>
    {
        _lastLat = c.Latitude;
        _lastLon = c.Longitude;

        Latitude = FormatLat(c.Latitude);
        Longitude = FormatLon(c.Longitude);
        AltitudeMsl = $"{c.AltitudeMslFeet:N0} ft";
        AltitudeAgl = $"{Math.Max(0, c.AltitudeAglFeet):N0} ft";
        Heading = $"{((int)Math.Round(c.HeadingTrueDeg)) % 360:000}°";
        Ias = $"{Math.Max(0, c.IasKnots):N0} kt";
        GroundSpeed = $"{Math.Max(0, c.GroundSpeedKnots):N0} kt";
        VerticalSpeed = c.VerticalSpeedFpm.ToString("+#,##0;-#,##0;0", CultureInfo.InvariantCulture) + " ft/min";
        GroundState = c.OnGround ? "AU SOL" : "EN VOL";
        Squawk = TransponderFormatter.Format(c.TransponderCode);
        NearestAirport = FormatNearestAirport(c.NearestAirportIcao, c.NearestAirportDistanceMeters);
    });

    private void OnAircraft(AircraftSnapshot a) => OnUi(() =>
    {
        // TITLE est le plus fiable ; sinon on retombe sur type/modèle.
        string title = !string.IsNullOrEmpty(a.Title) ? a.Title
                     : string.Join(' ', new[] { a.AtcType, a.AtcModel }.Where(x => x.Length > 0));
        AircraftTitle = string.IsNullOrEmpty(title) ? "Avion inconnu" : title;

        AircraftType = string.Join(" · ", new[] { a.AtcType, a.AtcModel }.Where(x => x.Length > 0));
        TailNumber = a.TailNumber;

        if (!string.IsNullOrEmpty(a.TailNumber))
            AddLog($"Aircraft: {AircraftTitle} ({a.TailNumber})", LogKind.System);
        else
            AddLog($"Aircraft: {AircraftTitle}", LogKind.System);
    });

    // ------------------------------------------------------------------ utilitaires

    private void OnUi(Action action) => _dispatcher.InvokeAsync(action);

    private void ResetContext()
    {
        AircraftTitle = "—"; AircraftType = ""; TailNumber = "";
        Ias = GroundSpeed = VerticalSpeed = AltitudeMsl = AltitudeAgl = Heading = "—";
        GroundState = Squawk = Latitude = Longitude = NearestAirport = "—";
    }

    private string FormatNearestAirport(string? icao, double distanceMeters)
    {
        if (string.IsNullOrEmpty(icao)) return "—";
        string? name = _stations.LookupAirportName(icao);
        string dist = distanceMeters < 1000
            ? $"{distanceMeters:N0} m"
            : $"{distanceMeters / 1000.0:N1} km";
        return name is not null ? $"{icao} · {name} ({dist})" : $"{icao} ({dist})";
    }

    private void AddLog(string text, LogKind kind)
    {
        Log.Insert(0, new LogEntryViewModel(DateTime.Now.ToString("HH:mm:ss"), text, kind));
        while (Log.Count > MaxLogEntries) Log.RemoveAt(Log.Count - 1);
    }

    private void AddSystemLog(string text) => AddLog(text, LogKind.System);

    private static string FormatLat(double lat) => $"{(lat >= 0 ? 'N' : 'S')} {Math.Abs(lat):F4}°";
    private static string FormatLon(double lon) => $"{(lon >= 0 ? 'E' : 'W')} {Math.Abs(lon):F4}°";

    // -------------------------------------------------- constructeur design-time (concepteur XAML)

    public MainViewModel()
        : this(new NullSimConnectService(), new NullStationResolver(), new NullAtcController(), new NullSpeechToText(), new FlightPlanStore(), Dispatcher.CurrentDispatcher)
    {
        HasFlightPlan = true;
        FlightPlanCallsign = "UAE231"; FlightPlanRoute = "OMDB → OMAA";
        FlightPlanCruise = "FL370"; FlightPlanAircraft = "B77W";
        State = ConnectionState.Connected;
        StatusText = "Connected (preview)";
        AtcStatus = "ATC: Ready";
        LastTranscript = "ready to push back";
        LastIntentText = "RequestPushback";
        LastDecisionText = "GRANTED — approved";
        LastDecisionApproved = true;
        PhaseDebugText = "Parked  ·  airborne=false  ·  sol";
        Com1.Active = "118.700"; Com1.Standby = "121.500"; Com1.IsTransmitting = true;
        Com1.Station = "Paris CDG · TWR";
        Com2.Active = "126.500"; Com2.Standby = "119.100";

        AircraftTitle = "Airbus A320neo Asobo"; AircraftType = "Airbus · A320"; TailNumber = "F-GKXA";
        Ias = "142 kt"; GroundSpeed = "138 kt"; VerticalSpeed = "+1 200 ft/min";
        AltitudeMsl = "5 400 ft"; AltitudeAgl = "5 100 ft"; Heading = "092°";
        GroundState = "EN VOL"; Squawk = "1200";
        Latitude = "N 48.8566°"; Longitude = "E 2.3522°";
        NearestAirport = "LFPG · Paris Charles de Gaulle (3.2 km)";

        Log.Clear();
        AddLog("F-GKXA, Paris CDG Tower, radio check, reading you five by five.", LogKind.Atc);
        AddLog("COM1 ACTIVE → 118.700", LogKind.Change);
        AddLog("COM1 ÉMISSION ON", LogKind.Transmit);
        AddLog("Avion : Airbus A320neo Asobo (F-GKXA)", LogKind.System);
        AddSystemLog("Connected to the simulator.");
    }
}
