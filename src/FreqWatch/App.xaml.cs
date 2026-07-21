using System.IO;
using System.Windows;
using FreqWatch.Atc;
using FreqWatch.Atc.Brain;
using FreqWatch.Atc.Context;
using FreqWatch.Atc.GroundServices;
using FreqWatch.Atc.Planning;
using FreqWatch.Atc.Understanding;
using FreqWatch.Audio;
using FreqWatch.Diagnostics;
using FreqWatch.Formatting;
using FreqWatch.Immersion;
using FreqWatch.Input;
using FreqWatch.Localization;
using FreqWatch.Settings;
using FreqWatch.Sim;
using FreqWatch.Stations;
using FreqWatch.ViewModels;

namespace FreqWatch;

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
    private TtsEngineSelector? _tts;
    private AtcController? _atc;
    private VoiceRepository? _voices;
    private FlightPlanStore? _plans;
    private FlightPlanImporter? _importer;
    private SpeechModelRepository? _whisper;
    private SherpaSpeechToText? _stt;
    private VoiceBus? _voice;
    private ImmersionController? _immersion;
    private CabinSoundPackRepository? _cabinPacks;
    private GlobalPushToTalk? _ptt;

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

        var stations = new OurAirportsStationResolver(Path.Combine(AppContext.BaseDirectory, "data"));

        // --- Boucle vocale ATC ---
        _voices = new VoiceRepository(_settings.Current.SherpaVoicesDir);
        _pipeline = new RadioAudioPipeline();
        _tts = new TtsEngineSelector(_settings, _voices);                        // Sherpa par défaut, repli Windows

        // Plan de vol (SimBrief) + indicatif parlé (télophonie compagnie / immat phonétique).
        var airlines = new AirlineTelephony();
        _plans = new FlightPlanStore();
        var callsigns = new CallsignFormatter(airlines, _plans);
        _importer = new FlightPlanImporter(new SimBriefClient(), _plans);

        var flightContext = new FlightContextProvider(stations);

        // Langue : anglais uniquement pour l'instant (ATC, copilote, trafic ambiant).
        var language = new LanguageResolver();

        var template = new TemplateAtcLineGenerator(language.Effective);
        var generator = new AtcLineGeneratorSelector(_settings, template);

        // Compréhension des requêtes pilote : on écoute dans VOTRE langue, pas celle de l'ATC.
        var intents = new IntentRecognizerSelector(_settings, new GrammarIntentRecognizer(language.UserLanguage));
        var brain = new AtcBrain(AtcRuleSet.Load(), stations, _plans, callsigns, _settings, language.Effective);

        // Services au sol : GSX (optionnel) déclenché quand l'ATC accorde le pushback.
        var groundServices = new GsxGroundServices(_sim, _settings);

        // Canal voix PARTAGÉ : ATC, copilote et trafic ambiant ne se coupent jamais.
        _voice = new VoiceBus(_pipeline, _settings);
        // Voix distinctes et stables par interlocuteur (contrôleur d'une fréquence, équipage…).
        var picker = new VoicePicker(_voices);

        _atc = new AtcController(_sim, stations, generator, _tts, _voice, picker, _settings, intents, brain, flightContext, callsigns, groundServices, _plans);

        // Immersion : copilote (annonces), trafic radio ambiant, packs de sons de cabine.
        _cabinPacks = new CabinSoundPackRepository(_settings.Current.CabinPacksDir);
        _immersion = new ImmersionController(_sim, flightContext, _tts, _voice, new CabinAudioPlayer(),
                                             _cabinPacks, _settings, language, picker);

        // Reconnaissance vocale (STT) : capture micro + ASR Whisper natif (offline), langue = celle de l'ATC.
        _whisper = new SpeechModelRepository(_settings.Current.SttModelsDir);
        _stt = new SherpaSpeechToText(_whisper, _settings, language.Effective);

        // --- UI ---
        var vm = new MainViewModel(_sim, stations, _atc, _stt, _plans, Dispatcher);
        vm.OpenSettingsRequested += () =>
        {
            var settingsWindow = new SettingsWindow(new SettingsViewModel(_settings!, _atc!, _voices!, _importer!, _whisper!, _cabinPacks!)) { Owner = MainWindow };
            settingsWindow.ShowDialog();
            vm.RefreshFromSettings(); // ré-aligne les toggles (Mode Test / ATC) modifiés dans les réglages
        };

        // Les échanges « IA » (trafic ambiant) et les annonces copilote apparaissent au journal.
        _immersion.CopilotSaid += text => vm.LogLine("COPILOT: " + text, LogKind.Copilot);
        _immersion.ChatterSaid += turn => vm.LogLine($"{turn.Speaker}: {turn.Text}", LogKind.Chatter);

        // Push-to-talk global : maintenir la touche configurée pour parler à l'ATC.
        _ptt = new GlobalPushToTalk(() => _settings!.Current.PttVirtualKey);
        _ptt.Pressed += vm.StartListening;
        _ptt.Released += vm.StopListeningAndSend;
        _ptt.Start();

        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();

        _sim.Start();
        _atc.Start();
        _immersion.Start();
        FileLog.Write("fenêtre affichée, services démarrés — initialisation terminée");

        // Premier lancement sans voix : téléchargement automatique (non bloquant).
        if (_settings.Current.TtsEngine == TtsEngineKind.Sherpa && !_voices.HasAnyVoice())
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
        _immersion?.Dispose();
        _atc?.Dispose();
        _voice?.Dispose();
        _stt?.Dispose();
        _tts?.Dispose();
        _pipeline?.Dispose();
        _sim?.Dispose();
        base.OnExit(e);
    }
}
