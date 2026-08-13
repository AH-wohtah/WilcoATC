using System.IO;
using System.Windows;
using WilcoATC.Atc;
using WilcoATC.Atc.Atis;
using WilcoATC.Atc.Brain;
using WilcoATC.Atc.Context;
using WilcoATC.Atc.GroundServices;
using WilcoATC.Atc.Planning;
using WilcoATC.Atc.Understanding;
using WilcoATC.Audio;
using WilcoATC.Diagnostics;
using WilcoATC.Formatting;
using WilcoATC.Immersion;
using WilcoATC.Input;
using WilcoATC.Localization;
using WilcoATC.Settings;
using WilcoATC.Sim;
using WilcoATC.Stations;
using WilcoATC.ViewModels;

namespace WilcoATC;

/// <summary>
/// Point de composition : couche SimConnect, stations, boucle vocale ATC (pipeline
/// audio + TTS sherpa-onnx + générateur) et ViewModel. Au premier lancement sans
/// voix installée, télécharge automatiquement la voix neuronale par défaut.
/// </summary>
public partial class App : Application
{
    private SettingsService? _settings;
    private SimConnectService? _sim;
    private RadioAudioPipeline? _pipeline;
    private RadioSampleRepository? _radioSamples;
    private Atc.Enroute.EnrouteSectorRepository? _sectors;
    private RunwayRepository? _runways;
    private Atc.Intercept.InterceptDirector? _intercept;
    private Sim.SimTitleCatalog? _titles;
    private Sim.SimTitleCollector? _titleCollector;
    private Traffic.TrafficPicture? _trafficPicture;
    private Traffic.TrafficAtcDirector? _trafficAtc;
    private Traffic.TrafficInjector? _injector;
    private TtsEngineSelector? _tts;
    private AtcController? _atc;
    private AtisDirector? _atis;
    private VoiceRepository? _voices;
    private FlightPlanStore? _plans;
    private FlightPlanImporter? _importer;
    private SpeechModelRepository? _whisper;
    private SherpaSpeechToText? _stt;
    private VoiceBus? _voice;
    private ImmersionController? _immersion;
    private CabinSoundPackRepository? _cabinPacks;
    private GlobalPushToTalk? _ptt;
    private GlobalJoystickButton? _joystickPtt;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Une exception sur le thread UI ferme l'app sans un mot : on la consigne d'abord.
        DispatcherUnhandledException += (_, args) =>
            FileLog.Exception("thread d'interface", args.Exception);

        FileLog.Write("démarrage de la composition");
        _settings = new SettingsService();

        // Localisation : anglais de base + langue choisie (interface ET ATC). Doit être fait
        // avant de créer les fenêtres (elles utilisent {DynamicResource S.*}).
        Loc.Initialize(_settings.Current.AppLanguage);

        _sim = new SimConnectService();

        // Résolution de station : fréquences LIVE du simulateur (SimConnect facility data) en
        // PRIORITÉ, jeu OurAirports (CSV, hors-ligne) en repli. Le décorateur enveloppe le CSV
        // et sert de source unique à toute l'app (panneau, ATC, voix).
        var csvStations = new OurAirportsStationResolver(Path.Combine(AppContext.BaseDirectory, "data"));
        // Le repli sur les fréquences RÉELLES est optionnel : coupé, l'ATC ne cite plus que ce
        // que le simulateur publie (voir AppSettings.UseRealWorldFrequencies).
        var stations = new SimStationResolver(csvStations, _sim,
                                              () => _settings!.Current.UseRealWorldFrequencies);

        // --- Boucle vocale ATC ---
        _voices = new VoiceRepository(_settings.Current.SherpaVoicesDir);
        // Échantillons radio déposés par l'utilisateur (déclic d'alternat, respiration,
        // queue de squelch, fond de cockpit). Dossier vide -> bruitages synthétisés.
        _radioSamples = new RadioSampleRepository(_settings.Current.RadioSamplesDir);
        _pipeline = new RadioAudioPipeline(_radioSamples);
        _tts = new TtsEngineSelector(_settings, _voices);                        // Sherpa par défaut, repli Windows

        // Plan de vol (SimBrief) + indicatif parlé (télophonie compagnie / immat phonétique).
        var airlines = new AirlineTelephony();
        _plans = new FlightPlanStore();
        var callsigns = new CallsignFormatter(airlines, _plans);
        _importer = new FlightPlanImporter(new SimBriefClient(), _plans);

        var flightContext = new FlightContextProvider(stations, _settings, _plans);

        // Langue du contrôleur : celle du PAYS survolé (préfixe OACI), et il bascule sur celle
        // du pilote dès que celui-ci lui parle. Le test de disponibilité des voix est ce qui
        // l'empêche de « parler » une langue qu'aucun modèle installé ne sait prononcer.
        var language = new LanguageResolver(_settings, lang => _voices!.HasVoiceFor(lang.Code()));

        // Génération et compréhension : DÉTERMINISTES, sans réseau. Les deux passaient
        // auparavant par un sélecteur qui interrogeait un LLM (Ollama / cloud) avant de
        // retomber ici — plusieurs secondes d'attente par transmission, pour un résultat
        // que ces deux classes produisent instantanément.
        var generator = new TemplateAtcLineGenerator(language.Effective);

        // La base compagnies sert dans les DEUX SENS : générer l'indicatif ATC (callsigns)
        // et reconnaître celui que le pilote prononce (spokenCallsigns).
        var spokenCallsigns = new SpokenCallsignResolver(airlines);
        var intents = new GrammarIntentRecognizer(language.UserLanguage, spokenCallsigns);
        // Pistes réelles (OurAirports) : l'ATC ne nomme que des pistes qui existent.
        _runways = new RunwayRepository();

        var brain = new AtcBrain(AtcRuleSet.Load(), stations, _plans, callsigns, _settings, language.Effective, _runways);

        // Services au sol : GSX (optionnel) déclenché quand l'ATC accorde le pushback.
        var groundServices = new GsxGroundServices(_sim, _settings);

        // Canal voix PARTAGÉ : ATC, copilote et trafic ambiant ne se coupent jamais.
        _voice = new VoiceBus(_pipeline, _settings);
        // Voix distinctes et stables par interlocuteur (contrôleur d'une fréquence, équipage…).
        var picker = new VoicePicker(_voices);

        // Secteurs ACC : installés à la demande depuis les réglages (rien n'est embarqué,
        // la source est sous licence CC BY-NC-SA — voir EnrouteSectorImporter).
        _sectors = new Atc.Enroute.EnrouteSectorRepository();

        // Titres de conteneur RÉELS de cette installation. Indispensable avant de faire naître
        // quoi que ce soit : un titre deviné échoue sans le moindre message, et ceux du disque
        // sont empaqueté dans des archives illisibles. Le simulateur reste la seule source.
        _titles = new Sim.SimTitleCatalog();
        _titleCollector = new Sim.SimTitleCollector(_sim, _titles);

        // Interception : la seule brique qui ÉCRIT dans le simulateur. Désactivée par défaut.
        _intercept = new Atc.Intercept.InterceptDirector(_sim, _settings, _titles);

        _atc = new AtcController(_sim, stations, generator, _tts, _voice, picker, _settings, intents, brain, flightContext, callsigns, groundServices, _plans, language, _sectors, _runways, _intercept);

        // ATIS : station du terrain, diffusée en boucle quand on se cale sur sa fréquence.
        // Volontairement HORS de l'AtcController : un ATIS ne répond à rien et n'attend aucun
        // collationnement — il partage seulement le canal voix, où l'ATC passe devant.
        _atis = new AtisDirector(_sim, stations, _plans, _tts, _voice, picker, _settings);

        // Immersion : copilote (annonces), trafic radio ambiant, packs de sons de cabine.
        _cabinPacks = new CabinSoundPackRepository(_settings.Current.CabinPacksDir);
        // Le trafic d'ambiance s'efface devant le trafic réel : la lambda est évaluée à chaque
        // échange, donc elle voit le directeur créé juste après.
        _immersion = new ImmersionController(_sim, flightContext, _tts, _voice, new CabinAudioPlayer(),
                                             _cabinPacks, _settings, language, picker,
                                             realTrafficOnAir: () => _trafficAtc?.IsVoicingRealTraffic == true);

        // Le contrôle parle au TRAFIC RÉEL : lecture seule du monde, puis autorisations
        // d'atterrissage et de décollage aux appareils qui s'y présentent vraiment. Ses
        // relevés alimentent aussi le catalogue de titres.
        _trafficPicture = new Traffic.TrafficPicture();
        _trafficAtc = new Traffic.TrafficAtcDirector(_sim, _trafficPicture, _runways, flightContext,
                                                     _tts, _voice, picker, _settings);

        // Injection : des appareils qui NAISSENT et que le simulateur pilote lui-même. Rien
        // n'est créé sans titre valide — d'où la dépendance au catalogue.
        _injector = new Traffic.TrafficInjector(_sim, _titles, _settings)
        {
            // Aucun appareil injecté ne doit porter l'indicatif du joueur : il répondrait
            // à sa place, et le contrôleur tiendrait l'échange pour fait.
            PlayerCallsign = () => flightContext.Current().Callsign,
        };

        // Reconnaissance vocale (STT) : capture micro + ASR Whisper natif (offline), langue = celle de l'ATC.
        _whisper = new SpeechModelRepository(_settings.Current.SttModelsDir);
        _stt = new SherpaSpeechToText(_whisper, _settings, language.Effective);

        // --- UI ---
        var vm = new MainViewModel(_sim, stations, _atc, _stt, _plans, _importer, callsigns, _settings, Dispatcher);

        // ViewModel de réglages : il alimente l'écran CFG de la fenêtre principale, seul
        // endroit où l'on règle quoi que ce soit (la fenêtre « Réglages avancés » a été
        // supprimée). Créé ici (composition root) car il exige tous les services.
        var settingsVm = new SettingsViewModel(
            _settings, _atc, _voices, _importer, _whisper, _cabinPacks, _tts, _voice, _radioSamples, _sectors);
        vm.Settings = settingsVm;

        // « Importer des fréquences » : choix d'un CSV (icao,type,mhz) validé, fusionné dans l'app.
        vm.ImportFrequenciesRequested += () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Fréquences (CSV)|*.csv|Tous les fichiers|*.*",
                Title = "Importer des fréquences (icao,type,mhz)",
            };
            if (dlg.ShowDialog(MainWindow) == true) vm.ImportFrequenciesFromFile(dlg.FileName);
        };


        // Les échanges « IA » (trafic ambiant) et les annonces copilote apparaissent au journal.
        _immersion.CopilotSaid += text => vm.LogLine("COPILOT: " + text, LogKind.Copilot);
        _immersion.ChatterSaid += turn => vm.LogLine($"{turn.Speaker}: {turn.Text}", LogKind.Chatter);

        // Les échanges avec le trafic RÉEL vont au même journal, sous la même catégorie : pour
        // le pilote, ce sont d'autres voix sur sa fréquence, et la distinction entre inventé et
        // réel ne le regarde pas. Sans ce câblage, faire taire le bavardage d'ambiance vidait
        // le journal — on n'entendait ET ne voyait plus rien.
        _trafficAtc.Said += (speaker, text) => vm.LogLine($"{speaker}: {text}", LogKind.Chatter);

        // DÉTRESSE : la fréquence appartient à l'appareil en difficulté. Le trafic et
        // l'ambiance se taisent — c'est ce que fait un vrai contrôleur devant un mayday.
        _trafficAtc.DistressInProgress = () => _atc?.RadioSilenceForDistress == true;
        _immersion.DistressInProgress = () => _atc?.RadioSilenceForDistress == true;

        // Le bulletin ATIS n'apparaît au journal qu'à sa PUBLICATION, pas à chaque passage :
        // il tourne en boucle, l'écrire à chaque tour noierait tout le reste.
        _atis.AtisPublished += text => vm.LogLine("ATIS: " + text, LogKind.Atis);

        // Push-to-talk global : maintenir la touche configurée pour parler à l'ATC.
        _ptt = new GlobalPushToTalk(() => _settings!.Current.PttVirtualKey);
        _ptt.Pressed += vm.StartListening;
        _ptt.Released += vm.StopListeningAndSend;
        _ptt.Start();

        // Variante JOYSTICK/HOTAS : même effet, sur un bouton de périphérique. Interrogé sur un
        // thread de fond -> on marshalle vers l'UI (la boucle STT touche des propriétés observables).
        _joystickPtt = new GlobalJoystickButton(
            () => (_settings!.Current.PttJoystickDevice, _settings.Current.PttJoystickButton));
        _joystickPtt.Pressed += () => Dispatcher.Invoke(vm.StartListening);
        _joystickPtt.Released += () => Dispatcher.Invoke(vm.StopListeningAndSend);
        _joystickPtt.Start();

        var window = new MainWindow { DataContext = vm };
        MainWindow = window;

        // Actions des réglages qui ont besoin d'une fenêtre (téléchargements, choix d'un OFP,
        // capture de la touche/du bouton push-to-talk) : elles vivent avec l'écran CFG.
        window.AttachSettings(settingsVm);

        // Assistant INTÉGRÉ (voile plein écran, plus de fenêtre séparée) : on lui injecte les
        // services, et sa fin (ou son saut) referme le voile et ré-aligne les toggles.
        window.OnboardingView.Attach(_settings, _voices, _whisper, _tts, _voice, _stt);
        window.OnboardingView.Completed += () => { vm.ShowOnboarding = false; vm.RefreshFromSettings(); };

        // Assistant de PREMIÈRE CONFIGURATION (langue, voix + reconnaissance, push-to-talk
        // clavier/manette) : voile plein écran affiché UNE SEULE FOIS, au tout premier lancement.
        window.SetupView.Attach(settingsVm, _settings, _voices, _whisper, _stt);
        window.SetupView.Completed += () =>
        {
            _settings.Current.SetupCompleted = true;
            _settings.Save();
            vm.ShowSetup = false;
            vm.RefreshFromSettings();
        };

        // PRÉREQUIS : sans voix ET sans modèle de reconnaissance, l'ATC ne peut ni parler ni
        // entendre. Le voile reste tant que les deux manquent — ce n'est pas un assistant que
        // l'on peut remettre à plus tard, c'est la condition de fonctionnement du logiciel.
        window.RequirementsGate.Attach(settingsVm, _settings, _voices, _whisper);
        window.RequirementsGate.Satisfied += () =>
        {
            vm.ShowRequirements = false;
            vm.RefreshFromSettings();
        };
        vm.ShowRequirements = !window.RequirementsGate.IsSatisfied;

        window.Show();

        // PREMIER LANCEMENT : la configuration passe AVANT tout (par-dessus le voile de vol).
        // Sauf si des modèles manquent : la barrière est alors prioritaire, l'assistant
        // supposant justement une installation déjà utilisable.
        if (!_settings.Current.SetupCompleted && !vm.ShowRequirements) vm.ShowSetup = true;

        _sim.Start();
        _atc.Start();
        _atis.Start();
        _immersion.Start();

        // Secteurs ACC : ~7 Mo de contours, 0,3 s à lire. On les charge en tâche de fond
        // maintenant, pour que ce ne soit pas le premier transfert vers un Centre qui paie.
        var sectors = _sectors;
        if (sectors is not null) _ = Task.Run(() => { try { _ = sectors.Count; } catch { } });
        FileLog.Write("fenêtre affichée, services démarrés — initialisation terminée");

        // Repli : sans voix installée, téléchargement automatique (non bloquant) — SAUF au tout
        // premier lancement, où c'est l'assistant de configuration qui s'en charge (pas de
        // fenêtre de téléchargement surgissant par-dessus le voile de config).
        if (_settings.Current.SetupCompleted
            && _settings.Current.TtsEngine == TtsEngineKind.Sherpa && !_voices.HasAnyVoice())
        {
            var download = new VoiceDownloadWindow(new VoiceDownloader(), VoiceRepository.DefaultVoiceUrl, _voices.VoicesDir)
            {
                Owner = window,
            };
            download.Show(); // non-modal : l'app reste utilisable pendant le téléchargement
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FileLog.Write($"fermeture (code {e.ApplicationExitCode})");
        _ptt?.Dispose();
        _joystickPtt?.Dispose();
        _immersion?.Dispose();
        _atis?.Dispose();
        _atc?.Dispose();
        _injector?.Dispose();
        _trafficAtc?.Dispose();
        _titleCollector?.Dispose();
        _voice?.Dispose();
        _stt?.Dispose();
        _tts?.Dispose();
        _pipeline?.Dispose();
        _sim?.Dispose();
        base.OnExit(e);
    }
}
