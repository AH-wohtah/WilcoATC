using System.IO;
using System.Windows;
using FreqWatch.Atc;
using FreqWatch.Atc.Brain;
using FreqWatch.Atc.Context;
using FreqWatch.Atc.GroundServices;
using FreqWatch.Atc.Planning;
using FreqWatch.Atc.Understanding;
using FreqWatch.Audio;
using FreqWatch.Formatting;
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
    private WhisperModelRepository? _whisper;
    private SherpaSpeechToText? _stt;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        // Langue effective (suit la voix TTS si le réglage est « Auto »).
        var language = new LanguageResolver(_settings);
        var template = new TemplateAtcLineGenerator(language.Effective);
        var generator = new AtcLineGeneratorSelector(_settings, template);

        // Plan de vol (SimBrief) + indicatif parlé (télophonie compagnie / immat phonétique).
        var airlines = new AirlineTelephony();
        _plans = new FlightPlanStore();
        var callsigns = new CallsignFormatter(airlines, _plans);
        _importer = new FlightPlanImporter(new SimBriefClient(), _plans);

        // Compréhension des requêtes pilote : contexte de vol + intention + cerveau de règles.
        var flightContext = new FlightContextProvider(stations);
        var intents = new IntentRecognizerSelector(_settings, new GrammarIntentRecognizer(language.Effective));
        var brain = new AtcBrain(AtcRuleSet.Load(), stations, _plans, callsigns, _settings, language.Effective);

        // Services au sol : GSX (optionnel) déclenché quand l'ATC accorde le pushback.
        var groundServices = new GsxGroundServices(_sim, _settings);

        _atc = new AtcController(_sim, stations, generator, _tts, _pipeline, _settings, intents, brain, flightContext, callsigns, groundServices, _plans);

        // Reconnaissance vocale (STT) : capture micro + ASR Whisper natif (offline), langue = celle de l'ATC.
        _whisper = new WhisperModelRepository(_settings.Current.SttModelsDir);
        _stt = new SherpaSpeechToText(_whisper, _settings, language.Effective);

        // --- UI ---
        var vm = new MainViewModel(_sim, stations, _atc, _stt, _plans, Dispatcher);
        vm.OpenSettingsRequested += () =>
        {
            var settingsWindow = new SettingsWindow(new SettingsViewModel(_settings!, _atc!, _voices!, _importer!, _whisper!)) { Owner = MainWindow };
            settingsWindow.ShowDialog();
            vm.RefreshFromSettings(); // ré-aligne les toggles (Mode Test / ATC) modifiés dans les réglages
        };

        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();

        _sim.Start();
        _atc.Start();

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
        _atc?.Dispose();
        _stt?.Dispose();
        _tts?.Dispose();
        _pipeline?.Dispose();
        _sim?.Dispose();
        base.OnExit(e);
    }
}
