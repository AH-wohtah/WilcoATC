using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using WilcoATC.Atc;
using WilcoATC.Atc.Brain;
using WilcoATC.Atc.Context;
using WilcoATC.Atc.Planning;
using WilcoATC.Atc.Understanding;
using WilcoATC.Common;
using WilcoATC.Diagnostics;
using WilcoATC.Formatting;
using WilcoATC.Localization;
using WilcoATC.Settings;
using WilcoATC.Sim;
using WilcoATC.Stations;

namespace WilcoATC.ViewModels;

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
    private readonly FlightPlanStore _plans;
    private readonly FlightPlanImporter _importer;
    private readonly CallsignFormatter _callsigns;
    private readonly SettingsService _settings;
    private readonly Dispatcher _dispatcher;

    private const int MaxLogEntries = 500;

    public ComRadioViewModel Com1 { get; } = new("COM 1");
    public ComRadioViewModel Com2 { get; } = new("COM 2");

    /// <summary>Journal, le plus récent en tête.</summary>
    public ObservableCollection<LogEntryViewModel> Log { get; } = new();

    // Commandes ATC.
    public ICommand TestTransmissionCommand { get; }

    /// <summary>Affiche les fréquences de l'aéroport saisi (onglet Fréquences).</summary>
    public ICommand SearchAirportCommand { get; }

    /// <summary>Revient à l'aéroport le plus proche et suit à nouveau le vol.</summary>
    public ICommand FollowNearestAirportCommand { get; }

    /// <summary>Importe le plan de vol depuis SimBrief (pseudo enregistré dans les Réglages).</summary>
    public ICommand ImportSimBriefCommand { get; }

    /// <summary>« Lancer un vol » : affiche l'assistant intégré (voile) par-dessus la fenêtre.</summary>
    public ICommand OpenOnboardingCommand { get; }

    /// <summary>Levé par « Importer des fréquences » (le choix du fichier est géré côté fenêtre).</summary>
    public event Action? ImportFrequenciesRequested;

    /// <summary>Importe un CSV de fréquences validées (corrections communautaires) dans l'app.</summary>
    public ICommand ImportFrequenciesCommand { get; }

    /// <summary>
    /// ViewModel des réglages. Alimente l'écran CFG, qui contient DÉSORMAIS TOUS les réglages
    /// (audio, voix, micro, ATC, immersion, transferts, langue…). Affecté par le point de composition
    /// après construction ; null au design-time (les liaisons CFG restent alors inertes).
    /// </summary>
    public SettingsViewModel? Settings { get; set; }

    public MainViewModel(ISimConnectService sim, IStationResolver stations, IAtcController atc,
                         ISpeechToText stt, FlightPlanStore plans, FlightPlanImporter importer,
                         CallsignFormatter callsigns, SettingsService settings, Dispatcher dispatcher)
    {
        _sim = sim;
        _stations = stations;
        _atc = atc;
        _stt = stt;
        _plans = plans;
        _importer = importer;
        _callsigns = callsigns;
        _settings = settings;
        _dispatcher = dispatcher;

        // Le voile « Lancer un vol » (porte d'entrée vers l'assistant) réapparaît À CHAQUE
        // lancement tant qu'on n'est pas en vol : _enteredApp démarre donc TOUJOURS à false et
        // ne passe à true qu'une fois l'assistant terminé/sauté dans cette session. (L'ancienne
        // init depuis OnboardingCompleted faisait disparaître l'assistant pour qui l'avait déjà fait.)
        _enteredApp = false;

        // Le vol saisi à la main est une donnée de SESSION (non persistée) : l'assistant repart
        // vide à chaque lancement — aucune reprise des réglages.

        plans.Changed += OnFlightPlanChanged;
        OnFlightPlanChanged(plans.Current);

        _sim.StateChanged += OnStateChanged;
        _sim.RadioSnapshotReceived += OnRadioSnapshot;
        _sim.RadioChanged += OnRadioChanged;
        _sim.ContextReceived += OnContext;
        _sim.AircraftReceived += OnAircraft;

        // Fréquences LIVE arrivées du simulateur pour un terrain -> rafraîchir le panneau.
        _stations.FrequenciesUpdated += OnStationFrequenciesUpdated;

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
        OpenOnboardingCommand = new RelayCommand(() => ShowOnboarding = true);
        ImportFrequenciesCommand = new RelayCommand(() => ImportFrequenciesRequested?.Invoke());
        ApplyManualFlightCommand = new RelayCommand(() => ApplyManualFlight());
        StartGateCommand = new RelayCommand(() => ManualStartPhase = 0);
        StartTaxiCommand = new RelayCommand(() => ManualStartPhase = 1);
        StartAirborneCommand = new RelayCommand(() => ManualStartPhase = 2);
        OpenReportCommand = new RelayCommand(OpenReport);
        CloseReportCommand = new RelayCommand(() => ShowReport = false);
        SubmitReportCommand = new RelayCommand(() => _ = SubmitReportAsync());
        SendPilotRequestCommand = new RelayCommand(SendPilotRequest);
        ToggleListenCommand = new RelayCommand(() => _ = ToggleListenAsync());
        SearchAirportCommand = new RelayCommand(SearchAirport);
        FollowNearestAirportCommand = new RelayCommand(FollowNearestAirport);
        ImportSimBriefCommand = new RelayCommand(() => _ = ImportSimBriefAsync());
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
            Raise(nameof(NoFlight));
            Raise(nameof(ShowLaunchVeil));
        }
    }

    public bool IsConnected => _state == ConnectionState.Connected;
    public bool IsWaiting => _state == ConnectionState.Waiting;
    public bool IsMissingDependency => _state == ConnectionState.MissingDependency;

    // Vrai seulement quand on est AUX COMMANDES (caméra cockpit/vol), pas au menu / carte du monde.
    private bool _inFlightSession;
    /// <summary>Le joueur est réellement dans l'avion / en vol (pas dans un menu du simulateur).</summary>
    public bool InFlightSession => _inFlightSession;

    /// <summary>Aucun vol en cours : simulateur déconnecté OU dans un menu (pas aux commandes).</summary>
    public bool NoFlight => _state != ConnectionState.Connected || !_inFlightSession;

    // Une fois l'assistant terminé/sauté, on ENTRE dans l'application : le voile ne réapparaît
    // plus (sinon on restait bloqué sur « No flight in progress » sans jamais voir le tableau
    // de bord). Les utilisateurs qui ont déjà fait l'assistant entrent directement.
    private bool _enteredApp;

    private bool _showSetup;
    /// <summary>Vrai quand l'assistant de PREMIÈRE CONFIGURATION (voile plein écran) est affiché — 1re fois uniquement.</summary>
    public bool ShowSetup { get => _showSetup; set => SetProperty(ref _showSetup, value); }

    private bool _showRequirements;
    /// <summary>
    /// Vrai tant qu'une voix de synthèse OU un modèle de reconnaissance manque.
    ///
    /// Ce voile n'est pas un assistant qu'on peut remettre à plus tard : sans ces deux
    /// modèles, le contrôleur ne peut ni parler ni entendre, et tout le reste de
    /// l'application est décoratif. Il se lève seulement quand les deux sont là.
    /// </summary>
    public bool ShowRequirements { get => _showRequirements; set => SetProperty(ref _showRequirements, value); }

    private bool _showOnboarding;
    /// <summary>Vrai quand l'assistant intégré (voile plein écran) est affiché par-dessus l'app.</summary>
    public bool ShowOnboarding
    {
        get => _showOnboarding;
        set
        {
            if (!SetProperty(ref _showOnboarding, value)) return;
            if (!value) _enteredApp = true;   // fin/saut de l'assistant -> on entre dans l'app
            Raise(nameof(ShowLaunchVeil));
        }
    }

    /// <summary>
    /// Voile « Lancer un vol » : porte d'ENTRÉE, affichée au lancement jusqu'à ce que
    /// l'utilisateur agisse (ouvre l'assistant ou entre dans l'app). Volontairement INDÉPENDANT
    /// de l'état du simulateur : le lier à <see cref="NoFlight"/> le faisait clignoter et
    /// disparaître au premier instantané de vol (~1 s après le lancement).
    /// </summary>
    public bool ShowLaunchVeil => !_showOnboarding && !_enteredApp;

    private void SetInFlightSession(bool value)
    {
        if (_inFlightSession == value) return;
        _inFlightSession = value;
        Raise(nameof(InFlightSession));
        Raise(nameof(NoFlight));
        Raise(nameof(ShowLaunchVeil));
    }

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

    private string _importStatus = "";
    /// <summary>Message de retour du dernier import SimBrief (vide au démarrage).</summary>
    public string ImportStatus
    {
        get => _importStatus;
        private set { if (SetProperty(ref _importStatus, value)) Raise(nameof(HasImportStatus)); }
    }

    public bool HasImportStatus => !string.IsNullOrEmpty(_importStatus);

    private async Task ImportSimBriefAsync()
    {
        ImportStatus = "Importing from SimBrief…";
        ImportStatus = await _importer.ImportFromSimBriefAsync(_settings.Current.SimBriefUsername);
    }

    // Plan courant + garde d'application manuelle (voir ApplyManualFlight).
    private FlightPlan? _currentPlan;
    private bool _applyingManual;

    private void OnFlightPlanChanged(FlightPlan? p) => OnUi(() =>
    {
        _currentPlan = p;
        HasFlightPlan = p is not null;

        if (p is not null)
        {
            FlightPlanCallsign = string.IsNullOrWhiteSpace(p.AtcCallsign)
                ? $"{p.AirlineIcao}{p.FlightNumber}" : p.AtcCallsign!;
            FlightPlanRoute = $"{p.OriginIcao} → {p.DestinationIcao}";
            FlightPlanCruise = FormatCruise(p.CruiseAltitudeFeet);
            FlightPlanAircraft = p.AircraftIcao ?? "—";

            // Plan venu de l'EXTÉRIEUR (SimBrief / OFP) : on remplit le formulaire de l'assistant
            // pour qu'il soit visible et éditable. (On saute cette recopie si c'est NOUS qui
            // venons de publier un plan manuel : inutile d'écraser ce que l'utilisateur a tapé.)
            if (!_applyingManual) FillFormFromPlan(p);
            AddSystemLog("Flight plan loaded: " + p.Summary);
        }

        UpdateFirstContact();
    });

    private static string FormatCruise(int feet)
    {
        if (feet <= 0) return "—";
        return feet >= 18000 ? $"FL{feet / 100:000}" : $"{feet:N0} ft";
    }

    // ================================================================== vol saisi à la main
    // L'assistant permet de renseigner soi-même son vol (alternative à SimBrief). Ces champs
    // sont persistés, et « appliquer » les reconstruit en plan de vol qui alimente tout l'ATC
    // (indicatif parlé, route, type) — exactement comme un import SimBrief.

    private string _manualCallsign = "";
    public string ManualCallsign { get => _manualCallsign; set => SetManualProp(ref _manualCallsign, value); }

    private string _manualAircraft = "";
    public string ManualAircraft { get => _manualAircraft; set => SetManualProp(ref _manualAircraft, value); }

    private string _manualOrigin = "";
    public string ManualOrigin { get => _manualOrigin; set => SetManualProp(ref _manualOrigin, value); }

    private string _manualDestination = "";
    public string ManualDestination { get => _manualDestination; set => SetManualProp(ref _manualDestination, value); }

    private int _manualStartPhase;
    /// <summary>0 = au parking (Delivery), 1 = au roulage (Ground), 2 = en vol (Center). Session uniquement.</summary>
    public int ManualStartPhase
    {
        get => _manualStartPhase;
        set
        {
            if (!SetProperty(ref _manualStartPhase, value)) return;
            Raise(nameof(IsStartGate)); Raise(nameof(IsStartTaxi)); Raise(nameof(IsStartAirborne));
            UpdateFirstContact();
        }
    }

    public bool IsStartGate => _manualStartPhase == 0;
    public bool IsStartTaxi => _manualStartPhase == 1;
    public bool IsStartAirborne => _manualStartPhase == 2;

    public ICommand StartGateCommand { get; }
    public ICommand StartTaxiCommand { get; }
    public ICommand StartAirborneCommand { get; }

    /// <summary>Applique le vol saisi (construit et publie un plan). Appelé en quittant l'étape « vol ».</summary>
    public ICommand ApplyManualFlightCommand { get; }

    private void SetManualProp(ref string field, string value)
    {
        if (!SetProperty(ref field, value ?? "")) return;
        UpdateFirstContact();
    }

    // Recopie un plan externe (SimBrief) dans le formulaire de l'assistant, sans le publier
    // ni le persister (donnée de session : perdue à la fermeture).
    private void FillFormFromPlan(FlightPlan p)
    {
        string cs = string.IsNullOrWhiteSpace(p.AtcCallsign)
            ? $"{p.AirlineIcao}{p.FlightNumber}" : p.AtcCallsign!;
        FillField(ref _manualCallsign, cs.Trim(), nameof(ManualCallsign));
        FillField(ref _manualAircraft, (p.AircraftIcao ?? "").Trim(), nameof(ManualAircraft));
        FillField(ref _manualOrigin, (p.OriginIcao ?? "").Trim(), nameof(ManualOrigin));
        FillField(ref _manualDestination, (p.DestinationIcao ?? "").Trim(), nameof(ManualDestination));
    }

    private void FillField(ref string field, string value, string prop)
    {
        if (field == value) return;
        field = value; Raise(prop);
    }

    /// <summary>
    /// Construit un plan de vol à partir du formulaire et le publie, SAUF si le plan courant y
    /// correspond déjà (SimBrief a rempli le formulaire sans qu'on y touche) : on garde alors le
    /// plan riche importé. <paramref name="force"/> force la (re)construction au démarrage.
    /// </summary>
    public void ApplyManualFlight(bool force = false)
    {
        string origin = Icao(_manualOrigin);

        // On publie dès qu'AU MOINS UN champ est saisi — surtout l'indicatif : sans plan, l'ATC
        // se rabattait sur l'immat/ATC-ID du simulateur (souvent le numéro de vol) au lieu de
        // l'indicatif parlé. Il ne faut donc PAS exiger l'aéroport de départ.
        bool empty = origin.Length == 0
                  && string.IsNullOrWhiteSpace(_manualCallsign)
                  && string.IsNullOrWhiteSpace(_manualDestination)
                  && string.IsNullOrWhiteSpace(_manualAircraft);
        if (!force && empty) { UpdateFirstContact(); return; }
        if (!force && _currentPlan is not null && SameAsForm(_currentPlan)) { UpdateFirstContact(); return; }

        var (airline, number) = CallsignFormatter.ParseAirlineFlight(_manualCallsign);
        string dest = Icao(_manualDestination);
        var oPos = origin.Length == 0 ? null : _stations.AirportPosition(origin);
        var dPos = dest.Length == 0 ? null : _stations.AirportPosition(dest);

        var plan = new FlightPlan
        {
            OriginIcao = origin.Length == 0 ? null : origin,
            OriginName = origin.Length == 0 ? null : _stations.LookupAirportName(origin),
            DestinationIcao = dest.Length == 0 ? null : dest,
            DestinationName = dest.Length == 0 ? null : _stations.LookupAirportName(dest),
            AirlineIcao = airline,
            FlightNumber = number,
            AtcCallsign = airline is null && !string.IsNullOrWhiteSpace(_manualCallsign) ? _manualCallsign.Trim().ToUpperInvariant() : null,
            AircraftIcao = string.IsNullOrWhiteSpace(_manualAircraft) ? null : _manualAircraft.Trim().ToUpperInvariant(),
            OriginLat = oPos?.Lat ?? 0, OriginLon = oPos?.Lon ?? 0,
            DestinationLat = dPos?.Lat ?? 0, DestinationLon = dPos?.Lon ?? 0,
        };

        _applyingManual = true;
        try { _plans.Set(plan); }
        finally { _applyingManual = false; }
    }

    // Le plan courant correspond-il déjà au formulaire ? (mêmes départ/arrivée/indicatif)
    private bool SameAsForm(FlightPlan p)
    {
        string cs = string.IsNullOrWhiteSpace(p.AtcCallsign) ? $"{p.AirlineIcao}{p.FlightNumber}" : p.AtcCallsign!;
        return Icao(p.OriginIcao) == Icao(_manualOrigin)
            && Icao(p.DestinationIcao) == Icao(_manualDestination)
            && Squash(cs) == Squash(_manualCallsign);
    }

    private static string Icao(string? s) => (s ?? "").Trim().ToUpperInvariant();
    private static string Squash(string? s) => (s ?? "").Replace(" ", "").ToUpperInvariant();

    // ------------------------------------------------------------------ onboarding : premier contact
    // Première fréquence et phrase d'essai, calculées à partir du VOL SAISI (ou, à défaut, du
    // plan / de l'aéroport le plus proche). À défaut de tout, un exemple parlant est conservé.

    private string _firstContactStation = "Paris De Gaulle — Delivery";
    public string FirstContactStation { get => _firstContactStation; private set => SetProperty(ref _firstContactStation, value); }

    private string _firstContactFreq = "121.605";
    public string FirstContactFreq { get => _firstContactFreq; private set => SetProperty(ref _firstContactFreq, value); }

    private string _firstCallPhrase = "Paris Delivery, Air France 1462, request IFR clearance to Toulouse.";
    public string FirstCallPhrase { get => _firstCallPhrase; private set => SetProperty(ref _firstCallPhrase, value); }

    private void UpdateFirstContact()
    {
        // Aéroport de départ : saisi à la main en priorité, sinon le terrain affiché (le plus proche).
        string origin = Icao(_manualOrigin);
        if (origin.Length == 0) origin = Icao(_frequenciesIcao);
        if (origin.Length == 0) return; // rien -> on garde l'exemple

        string name = FlightPlan.CleanAirportName(_stations.LookupAirportName(origin)) ?? origin;

        // Fréquence du terrain de départ : Clairance (Delivery) sinon Sol sinon Tour.
        string word = "Delivery";
        double? hz = _stations.FindFrequencyHz(origin, ControllerType.Clearance);
        if (hz is null) { hz = _stations.FindFrequencyHz(origin, ControllerType.Ground); word = "Ground"; }
        if (hz is null) { hz = _stations.FindFrequencyHz(origin, ControllerType.Tower);  word = "Tower"; }
        FirstContactStation = $"{name} — {word}";
        FirstContactFreq = hz is not null ? FrequencyFormatter.FormatMHz(hz.Value) : "—";

        // Indicatif parlé (télophonie compagnie ou immat épelée) depuis ce qui est saisi.
        string raw = _manualCallsign.Length > 0 ? _manualCallsign
                   : (_flightPlanCallsign is "—" or "" ? "" : _flightPlanCallsign);
        string spoken = raw.Length == 0 ? "" : _callsigns.SpeakCallsign(raw);
        string cs = spoken.Length == 0 ? "" : spoken + ", ";

        string dest = DestinationDisplayName();
        string destPart = dest.Length == 0 ? "" : " to " + dest;

        // La phrase à lire suit le moment choisi (parking / roulage / en vol).
        FirstCallPhrase = _manualStartPhase switch
        {
            1 => $"{name} Ground, {cs}request taxi.",
            2 => $"{name} Center, {cs}with you{destPart}.",
            _ => $"{name} Delivery, {cs}request IFR clearance{destPart}.",
        };
    }

    // Nom parlable de la destination : saisie à la main, sinon déduite de « OACI → OACI ».
    private string DestinationDisplayName()
    {
        string icao = Icao(_manualDestination);
        if (icao.Length == 0)
        {
            var parts = _flightPlanRoute.Split('→');
            if (parts.Length >= 2) icao = Icao(parts[1]);
        }
        if (icao.Length == 0 || icao == "—") return "";
        return FlightPlan.CleanAirportName(_stations.LookupAirportName(icao)) ?? icao;
    }

    // ================================================================== signalement de fréquence
    // Bouton près de la note de source (OurAirports) : signaler une fréquence MANQUANTE ou
    // INCORRECTE. Le rapport (aéroport OACI + fréquence + détails) part vers un webhook Discord.

    private readonly FrequencyReporter _reporter = new();

    public ICommand OpenReportCommand { get; }
    public ICommand CloseReportCommand { get; }
    public ICommand SubmitReportCommand { get; }

    private bool _showReport;
    public bool ShowReport { get => _showReport; set => SetProperty(ref _showReport, value); }

    private string _reportDiscordUser = "";
    /// <summary>Nom d'utilisateur Discord de la personne qui signale (pour créditer/recontacter).</summary>
    public string ReportDiscordUser { get => _reportDiscordUser; set => SetProperty(ref _reportDiscordUser, value); }

    private string _reportAirport = "";
    public string ReportAirport { get => _reportAirport; set => SetProperty(ref _reportAirport, value); }

    private string _reportFrequency = "";
    public string ReportFrequency { get => _reportFrequency; set => SetProperty(ref _reportFrequency, value); }

    private string _reportStatus = "";
    public string ReportStatus
    {
        get => _reportStatus;
        private set { if (SetProperty(ref _reportStatus, value)) Raise(nameof(HasReportStatus)); }
    }
    public bool HasReportStatus => !string.IsNullOrEmpty(_reportStatus);

    private bool _reportSending;

    private void OpenReport()
    {
        ReportAirport = _frequenciesIcao ?? ""; // pré-rempli avec le terrain affiché
        ReportFrequency = "";
        ReportDiscordUser = "";
        ReportStatus = "";
        ShowReport = true;
    }

    private async Task SubmitReportAsync()
    {
        if (_reportSending) return;

        string airport = (_reportAirport ?? "").Trim().ToUpperInvariant();
        if (airport.Length == 0) { ReportStatus = Loc.T("S.Rep.NeedAirport"); return; }

        _reportSending = true;
        ReportStatus = Loc.T("S.Rep.Sending");

        bool ok = await _reporter.SendAsync(new FrequencyReport(
            (_reportDiscordUser ?? "").Trim(), airport, (_reportFrequency ?? "").Trim()));

        _reportSending = false;
        if (ok) { ReportStatus = ""; ShowReport = false; AddSystemLog($"Frequency report sent for {airport}."); }
        else ReportStatus = Loc.T("S.Rep.Failed");
    }

    // ================================================================== import de fréquences
    // Bouton des réglages : importer un CSV (icao,type,mhz) de fréquences validées — pour que les
    // corrections/ajouts revenus des signalements soient réintégrés dans le logiciel.

    private string _importFreqStatus = "";
    public string ImportFreqStatus
    {
        get => _importFreqStatus;
        private set { if (SetProperty(ref _importFreqStatus, value)) Raise(nameof(HasImportFreqStatus)); }
    }
    public bool HasImportFreqStatus => !string.IsNullOrEmpty(_importFreqStatus);

    /// <summary>Fusionne le CSV choisi dans l'overlay utilisateur, recharge, et rafraîchit le panneau.</summary>
    public void ImportFrequenciesFromFile(string path)
    {
        try
        {
            int n = _stations.ImportOverlay(path);
            PopulateFrequencies(_frequenciesIcao);   // rafraîchit immédiatement le terrain affiché
            ImportFreqStatus = string.Format(Loc.T("S.Cfg.ImportDone"), n);
        }
        catch (Exception ex)
        {
            ImportFreqStatus = Loc.T("S.Cfg.ImportFail");
            FileLog.Exception("import de fréquences", ex);
        }
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
        if (_isListening) return;

        // MODÈLE ABSENT : on le DIT. Se contenter de ne rien faire laissait l'utilisateur
        // appuyer sur son alternat sans le moindre retour — indiscernable d'un micro muet,
        // d'une touche mal assignée ou d'un plantage. Trois causes, aucun indice.
        if (!_stt.IsAvailable)
        {
            AtcStatus = Loc.T("S.Atc.StatusPrefix") + Loc.T("S.Status.NoSpeechModel");
            return;
        }

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

    // ------------------------------------------------------------------ fréquences du terrain

    /// <summary>Toutes les fréquences publiées de l'aéroport courant (liste plate, pour la mise
    /// en évidence « calé dessus »). L'UI, elle, affiche <see cref="AirportFrequencyGroups"/>.</summary>
    public ObservableCollection<AirportFrequencyViewModel> AirportFrequencies { get; } = new();

    /// <summary>Les mêmes fréquences RANGÉES par catégorie (Sol, Tour, Approche…) pour l'affichage.</summary>
    public ObservableCollection<FrequencyGroupViewModel> AirportFrequencyGroups { get; } = new();

    private string _airportFrequenciesTitle = "—";
    /// <summary>En-tête du panneau : « EBBR · Brussels ».</summary>
    public string AirportFrequenciesTitle
    {
        get => _airportFrequenciesTitle;
        private set => SetProperty(ref _airportFrequenciesTitle, value);
    }

    private bool _hasAirportFrequencies;
    public bool HasAirportFrequencies
    {
        get => _hasAirportFrequencies;
        private set => SetProperty(ref _hasAirportFrequencies, value);
    }

    private string _airportFrequenciesEmpty = "";
    /// <summary>Message affiché quand la liste est vide — on explique POURQUOI.</summary>
    public string AirportFrequenciesEmpty
    {
        get => _airportFrequenciesEmpty;
        private set => SetProperty(ref _airportFrequenciesEmpty, value);
    }

    // ICAO dont la liste est actuellement affichée : la recherche balaye tout le jeu de
    // fréquences, on ne la refait donc QUE lorsque l'aéroport change (le contexte arrive
    // à 1 Hz, l'aéroport le plus proche ne bouge, lui, que de loin en loin).
    private string? _frequenciesIcao;

    // Aéroport ÉPINGLÉ par l'utilisateur. null = on suit l'aéroport le plus proche.
    // C'est ce qui permet de consulter le terrain de destination — ou n'importe lequel —
    // sans avoir à y être.
    private string? _pinnedIcao;

    // Dernier aéroport signalé par le simulateur, mémorisé même quand on affiche autre
    // chose : sans ça, « revenir au plus proche » n'aurait rien à afficher.
    private string? _nearestIcao;

    private string _airportSearchText = "";
    /// <summary>Saisie du champ de recherche (code OACI).</summary>
    public string AirportSearchText
    {
        get => _airportSearchText;
        set => SetProperty(ref _airportSearchText, value);
    }

    private string _airportSearchStatus = "";
    /// <summary>Message de la recherche (aéroport introuvable…), vide si tout va bien.</summary>
    public string AirportSearchStatus
    {
        get => _airportSearchStatus;
        private set => SetProperty(ref _airportSearchStatus, value);
    }

    private bool _isAirportPinned;
    /// <summary>Vrai quand on consulte un aéroport choisi et non celui où l'on se trouve.</summary>
    public bool IsAirportPinned
    {
        get => _isAirportPinned;
        private set => SetProperty(ref _isAirportPinned, value);
    }

    /// <summary>Le simulateur signale un nouvel aéroport proche.</summary>
    private void OnNearestAirportChanged(string? icao)
    {
        _nearestIcao = icao;
        if (_pinnedIcao is null) ShowFrequenciesFor(icao);
    }

    /// <summary>Affiche les fréquences de l'aéroport saisi. Saisie vide = retour au plus proche.</summary>
    private void SearchAirport()
    {
        string icao = (AirportSearchText ?? "").Trim().ToUpperInvariant();

        if (icao.Length == 0) { FollowNearestAirport(); return; }

        // On n'accepte que ce que le jeu de données connaît : épingler un code inexistant
        // afficherait une liste vide sans expliquer pourquoi.
        if (_stations.LookupAirportName(icao) is null)
        {
            AirportSearchStatus = string.Format(Loc.T("S.Freqs.NotFound"), icao);
            return;
        }

        AirportSearchStatus = "";
        _pinnedIcao = icao;
        IsAirportPinned = true;
        ShowFrequenciesFor(icao);
    }

    /// <summary>Revient à l'aéroport le plus proche et suit à nouveau le vol.</summary>
    private void FollowNearestAirport()
    {
        _pinnedIcao = null;
        IsAirportPinned = false;
        AirportSearchStatus = "";
        AirportSearchText = "";
        ShowFrequenciesFor(_nearestIcao);
    }

    private void ShowFrequenciesFor(string? icao)
    {
        if (string.Equals(icao, _frequenciesIcao, StringComparison.OrdinalIgnoreCase)) return;
        _frequenciesIcao = icao;
        PopulateFrequencies(icao);
    }

    /// <summary>
    /// Remplit le panneau depuis le résolveur (fréquences LIVE du simulateur en priorité, CSV
    /// en repli). Séparé de <see cref="ShowFrequenciesFor"/> pour pouvoir RE-remplir le même
    /// terrain à l'arrivée des fréquences live, sans être bloqué par le garde d'égalité.
    /// </summary>
    private void PopulateFrequencies(string? icao)
    {
        AirportFrequencies.Clear();
        AirportFrequencyGroups.Clear();

        if (string.IsNullOrWhiteSpace(icao))
        {
            AirportFrequenciesTitle = "—";
            AirportFrequenciesEmpty = Loc.T("S.Freqs.NoAirport");
            HasAirportFrequencies = false;
            return;
        }

        string? name = FlightPlan.CleanAirportName(_stations.LookupAirportName(icao!));
        AirportFrequenciesTitle = string.IsNullOrWhiteSpace(name) ? icao! : $"{icao} · {name}";

        foreach (var f in _stations.ListFrequencies(icao!))
            AirportFrequencies.Add(new AirportFrequencyViewModel(f));

        foreach (var group in GroupFrequencies(AirportFrequencies))
            AirportFrequencyGroups.Add(group);

        HasAirportFrequencies = AirportFrequencies.Count > 0;
        AirportFrequenciesEmpty = HasAirportFrequencies ? "" : Loc.T("S.Freqs.None");

        MarkTunedFrequencies();
        UpdateFirstContact();
    }

    /// <summary>
    /// Range les fréquences par CATÉGORIE dans l'ordre d'un vol (ATIS → Clairance → Sol → Tour →
    /// Départ → Approche → Centre), puis les entrées « texte libre » (Ramp, Radio…) à la fin.
    /// </summary>
    private static IEnumerable<FrequencyGroupViewModel> GroupFrequencies(
        IEnumerable<AirportFrequencyViewModel> freqs)
        => freqs
            // Clé : le TYPE reconnu (un groupe par position), sinon le libellé libre (RMP, RADIO…).
            .GroupBy(f => f.Type == ControllerType.Unknown ? "U:" + f.Label : "T:" + (int)f.Type)
            .OrderBy(g => ControllerTaxonomy.SortRank(g.First().Type))   // 0..6 reconnus, 7 = libre
            .ThenBy(g => g.First().Label, StringComparer.Ordinal)        // départage les groupes libres
            .Select(g => new FrequencyGroupViewModel(
                CategoryHeader(g.First()),
                g.OrderBy(f => f.Mhz).ToList()));

    // En-tête de section. Termes radio standard (anglais, comme le reste du panneau) pour les
    // positions reconnues ; le libellé brut pour le texte libre (RMP, RADIO, UNICOM…).
    private static string CategoryHeader(AirportFrequencyViewModel f) => f.Type switch
    {
        ControllerType.Atis => "ATIS",
        ControllerType.Clearance => "CLEARANCE",
        ControllerType.Ground => "GROUND",
        ControllerType.Tower => "TOWER",
        ControllerType.Departure => "DEPARTURE",
        ControllerType.Approach => "APPROACH",
        ControllerType.Center => "CENTER",
        _ => f.Label,
    };

    // Fréquences LIVE arrivées pour un terrain (réponse SimConnect, sur le thread de pompe) :
    // rafraîchir le panneau s'il l'affiche, et réévaluer la station calée sur chaque COM — une
    // fréquence jusque-là « non répertoriée » peut désormais se résoudre proprement.
    private void OnStationFrequenciesUpdated(string icao) => OnUi(() =>
    {
        if (string.Equals(icao, _frequenciesIcao, StringComparison.OrdinalIgnoreCase))
            PopulateFrequencies(_frequenciesIcao);

        Com1.Station = ResolveStationLabel(_com1ActiveHz);
        Com2.Station = ResolveStationLabel(_com2ActiveHz);
    });

    /// <summary>
    /// Met en évidence la ligne sur laquelle une radio est calée. On compare au CANAL
    /// (tolérance ±500 Hz) et non à l'égalité stricte : l'espacement 8.33 kHz et les
    /// arrondis flottants feraient échouer un test d'égalité.
    /// </summary>
    private void MarkTunedFrequencies()
    {
        foreach (var row in AirportFrequencies)
        {
            double hz = row.Mhz * 1_000_000.0;
            row.IsTuned = FrequencyFormatter.SameChannel(hz, _com1ActiveHz)
                       || FrequencyFormatter.SameChannel(hz, _com2ActiveHz);
        }
    }

    private double _com1ActiveHz, _com2ActiveHz;

    /// <summary>
    /// Libellé de station pour la fréquence COURANTE d'une radio COM.
    ///
    /// Priorité au jeu de données OurAirports (nom d'aéroport + type de contrôleur). À
    /// défaut, si la fréquence est une VHF aviation civile VALIDE (118–137 MHz) mais
    /// simplement ABSENTE du CSV — fréquence non répertoriée, espacement 8.33 kHz, terrain
    /// récent, couverture OurAirports lacunaire hors USA — on NE laisse PAS le champ vide :
    /// on se rabat sur l'aéroport le plus proche en signalant clairement que la fréquence
    /// n'est pas répertoriée. Même repli que la boucle voix ATC (AtcController.ControllerLabel),
    /// pour que le panneau COM ne CONTREDISE plus le simulateur.
    ///
    /// Hors bande aviation (NAV, fréquence de garde, radio non initialisée) : pas de station
    /// à revendiquer, on renvoie null (aucun libellé affiché).
    /// </summary>
    private string? ResolveStationLabel(double hz)
    {
        string? resolved = _stations.Resolve(hz, _lastLat, _lastLon);
        if (!string.IsNullOrWhiteSpace(resolved)) return resolved;

        if (!IsAviationComBand(hz)) return null;

        string? airport = FlightPlan.CleanAirportName(
            _nearestIcao is null ? null : _stations.LookupAirportName(_nearestIcao));

        return string.IsNullOrWhiteSpace(airport)
            ? Loc.T("S.Station.Unlisted")
            : string.Format(Loc.T("S.Station.NearUnlisted"), airport);
    }

    /// <summary>Bande VHF aviation civile : 118,000 à 136,990 MHz (espacements 25 et 8,33 kHz).</summary>
    private static bool IsAviationComBand(double hz)
    {
        double mhz = hz / 1_000_000.0;
        return mhz >= 118.0 && mhz < 137.0;
    }

    // ------------------------------------------------------------------ handlers (marshalés UI)

    private void OnStateChanged(ConnectionState state, string? detail) => OnUi(() =>
    {
        // La couche SimConnect re-signale « en attente » en boucle tant que le simulateur
        // n'est pas là : on ne journalise donc QUE les vraies transitions d'état, sinon le
        // journal se remplissait de « waiting for the simulator » à l'infini.
        bool changed = state != _state;

        State = state;
        StatusText = detail ?? state.ToString();

        // Déconnexion / attente : on n'est plus aux commandes.
        if (state != ConnectionState.Connected) SetInFlightSession(false);

        switch (state)
        {
            case ConnectionState.Waiting:
                Com1.Active = Com1.Standby = "---.---";
                Com2.Active = Com2.Standby = "---.---";
                Com1.IsTransmitting = Com2.IsTransmitting = false;
                Com1.Station = Com2.Station = null;
                ResetContext();
                break;
            case ConnectionState.Connected:
                if (changed) AddSystemLog(detail ?? "Connected to the simulator.");
                // Ré-interroge le terrain affiché : déclenche la demande de fréquences LIVE
                // (utile si un aéroport était épinglé AVANT la connexion).
                PopulateFrequencies(_frequenciesIcao);
                break;
            case ConnectionState.MissingDependency:
                if (changed) AddSystemLog("Missing dependency: " + detail);
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
        // Une fréquence VHF aviation VALIDE mais absente du CSV se rabat sur un libellé de
        // secours (cf. ResolveStationLabel) au lieu de rester vide : le vide donnait à tort
        // l'impression que le logiciel jugeait « invalide » une fréquence pourtant réelle
        // dans le simulateur (jeu de données OurAirports incomplet hors USA).
        Com1.Station = ResolveStationLabel(s.Com1ActiveHz);
        Com2.Station = ResolveStationLabel(s.Com2ActiveHz);

        // Surligne, dans le panneau du terrain, la ligne sur laquelle on est calé.
        _com1ActiveHz = s.Com1ActiveHz;
        _com2ActiveHz = s.Com2ActiveHz;
        MarkTunedFrequencies();
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
        SetInFlightSession(c.InFlightSession);
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
        // Aéroport « opérationnel » : le plus proche signalé par le simulateur, SAUF s'il n'a
        // aucune fréquence publiée (base militaire mitoyenne type EBMB/Melsbroek) — on prend
        // alors l'aéroport à fréquences co-localisé (EBBR/Bruxelles). Même valeur pour
        // l'affichage « aéroport le plus proche » ET le panneau des fréquences suivi.
        string? opIcao = _stations.OperationalAirport(c.NearestAirportIcao, c.Latitude, c.Longitude);
        double opDist = string.Equals(opIcao, c.NearestAirportIcao, StringComparison.OrdinalIgnoreCase)
            ? c.NearestAirportDistanceMeters
            : DistanceToAirport(opIcao, c.Latitude, c.Longitude) ?? c.NearestAirportDistanceMeters;

        NearestAirport = FormatNearestAirport(opIcao, opDist);
        OnNearestAirportChanged(opIcao);
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
        _com1ActiveHz = _com2ActiveHz = 0;
        // On NE touche PAS à l'aéroport épinglé : une déconnexion du simulateur ne doit pas
        // effacer la fiche que l'utilisateur est en train de consulter.
        OnNearestAirportChanged(null);
    }

    /// <summary>Distance (m) de l'avion à un aéroport, ou null si sa position est inconnue.</summary>
    private double? DistanceToAirport(string? icao, double lat, double lon)
    {
        if (string.IsNullOrWhiteSpace(icao)) return null;
        var pos = _stations.AirportPosition(icao!);
        return pos is { } p ? Geo.DistanceMeters(lat, lon, p.Lat, p.Lon) : null;
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
        : this(new NullSimConnectService(), new NullStationResolver(), new NullAtcController(),
               new NullSpeechToText(), new FlightPlanStore(),
               new FlightPlanImporter(new SimBriefClient(), new FlightPlanStore()),
               new CallsignFormatter(new AirlineTelephony(), new FlightPlanStore()),
               new SettingsService(), Dispatcher.CurrentDispatcher)
    {
        HasFlightPlan = true;
        FlightPlanCallsign = "UAE231"; FlightPlanRoute = "OMDB → OMAA";
        FlightPlanCruise = "FL370"; FlightPlanAircraft = "B77W";
        _enteredApp = true; _inFlightSession = true; // aperçu concepteur : app visible, sans voile
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
