using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using WilcoATC.Atc;
using WilcoATC.Atc.Enroute;
using WilcoATC.Atc.Planning;
using WilcoATC.Audio;
using WilcoATC.Diagnostics;
using WilcoATC.Immersion;
using WilcoATC.Input;
using WilcoATC.Localization;
using WilcoATC.Settings;

namespace WilcoATC.ViewModels;

/// <summary>
/// ViewModel des réglages. Les setters écrivent directement dans
/// <see cref="SettingsService.Current"/> (appliqué à la volée) et la persistance est
/// AUTOMATIQUE : l'écran CFG de la fenêtre principale n'a plus de bouton « Enregistrer »
/// (voir <c>_autoSave</c>), puisqu'il n'y a plus de fenêtre de réglages à valider.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IAtcController _atc;
    private readonly VoiceRepository _voices;
    private readonly FlightPlanImporter _importer;
    private readonly SpeechModelRepository _whisper;
    private readonly CabinSoundPackRepository _cabinPacks;
    private readonly ITtsEngine _tts;
    private readonly VoiceBus _voice;
    private readonly RadioSampleRepository _radioSamples;
    private readonly EnrouteSectorRepository _sectors;

    private AppSettings S => _settings.Current;

    /// <summary>Sauvegarde différée (voir le constructeur) : plus de bouton « Enregistrer ».</summary>
    private readonly DispatcherTimer _autoSave = new() { Interval = TimeSpan.FromMilliseconds(600) };

    /// <summary>Levé par le bouton « Télécharger cette voix » (l'ouverture est gérée côté fenêtre).</summary>
    public event Action<CatalogVoice>? DownloadVoiceRequested;

    /// <summary>Levé par « Tout télécharger » : la liste des voix encore absentes.</summary>
    public event Action<IReadOnlyList<CatalogVoice>>? DownloadAllVoicesRequested;

    /// <summary>Levé par le bouton « Charger un OFP » (dialogue fichier géré côté fenêtre).</summary>
    public event Action? LoadOfpRequested;

    /// <summary>Levé par le bouton « Télécharger le modèle vocal » (progression gérée côté fenêtre).</summary>
    public event Action? DownloadSpeechModelRequested;

    public SettingsViewModel(SettingsService settings, IAtcController atc, VoiceRepository voices,
                             FlightPlanImporter importer, SpeechModelRepository whisper,
                             CabinSoundPackRepository cabinPacks, ITtsEngine tts, VoiceBus voice,
                             RadioSampleRepository radioSamples, EnrouteSectorRepository sectors)
    {
        _sectors = sectors;
        _settings = settings;
        _atc = atc;
        _voices = voices;
        _importer = importer;
        _whisper = whisper;
        _cabinPacks = cabinPacks;
        _tts = tts;
        _voice = voice;
        _radioSamples = radioSamples;

        OutputDevices = AudioDeviceService.GetOutputDevices();
        _selectedDevice = OutputDevices.FirstOrDefault(d => d.Number == S.OutputDeviceNumber) ?? OutputDevices[0];
        InputDevices = AudioDeviceService.GetInputDevices();
        _selectedInputDevice = InputDevices.FirstOrDefault(d => d.Number == S.InputDeviceNumber) ?? InputDevices[0];

        foreach (var v in new WindowsTtsEngine(() => null).GetVoices())
            Voices.Add(v);
        RefreshSherpaVoices();

        _selectedCatalogVoice = CatalogVoices[0];

        // Persistance automatique : toute écriture de propriété replanifie une sauvegarde
        // différée. Le délai groupe les rafales (glissement d'un curseur = une seule écriture).
        _autoSave.Tick += (_, _) => { _autoSave.Stop(); _settings.Save(); };
        PropertyChanged += (_, _) => { _autoSave.Stop(); _autoSave.Start(); };

        TestVoiceCommand = new RelayCommand(() => _ = TestVoiceAsync());
        DownloadVoiceCommand = new RelayCommand(() => DownloadVoiceRequested?.Invoke(SelectedCatalogVoice));
        DownloadAllVoicesCommand = new RelayCommand(() => DownloadAllVoicesRequested?.Invoke(MissingVoices()));
        DownloadEnrouteSectorsCommand = new RelayCommand(() => _ = InstallEnrouteSectorsAsync());
        OpenVoicesFolderCommand = new RelayCommand(OpenVoicesFolder);
        ImportSimBriefCommand = new RelayCommand(() => _ = ImportSimBriefAsync());
        LoadOfpCommand = new RelayCommand(() => LoadOfpRequested?.Invoke());
        DownloadSpeechModelCommand = new RelayCommand(() => DownloadSpeechModelRequested?.Invoke());
        OpenLanguagesFolderCommand = new RelayCommand(OpenLanguagesFolder);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);
        RefreshLanguagesCommand = new RelayCommand(() => { AvailableLanguages = Loc.Available(); Raise(nameof(SelectedAppLanguage)); });
        OpenCabinFolderCommand = new RelayCommand(OpenCabinFolder);
        OpenRadioSamplesFolderCommand = new RelayCommand(OpenRadioSamplesFolder);
        // Relire le dossier après y avoir déposé des fichiers, sans relancer l'application.
        RefreshRadioSamplesCommand = new RelayCommand(() =>
        {
            _radioSamples.Refresh();
            Raise(nameof(RadioSamplesState));
        });
        CapturePttCommand = new RelayCommand(() => CapturePttRequested?.Invoke());
        ClearPttCommand = new RelayCommand(() =>
        {
            S.PttVirtualKey = 0; S.PttKeyName = ""; _settings.Save(); Raise(nameof(PttKeyDisplay));
        });
        CaptureJoystickCommand = new RelayCommand(() => { PttJoystickCapturing = true; CaptureJoystickRequested?.Invoke(); });
        ClearJoystickCommand = new RelayCommand(() =>
        {
            S.PttJoystickDevice = -1; S.PttJoystickButton = 0; S.PttJoystickName = "";
            _settings.Save(); Raise(nameof(PttJoystickDisplay));
        });
        RefreshCabinPacksCommand = new RelayCommand(() => { CabinPacks = _cabinPacks.List(); Raise(nameof(SelectedCabinPack)); });
        // Relancer l'assistant : on le REARME et on persiste. Il s'ouvrira au prochain
        // démarrage — le rouvrir séance tenante par-dessus la boîte de réglages donnerait
        // deux fenêtres modales empilées, et l'assistant écrit dans les mêmes réglages.
        RestartOnboardingCommand = new RelayCommand(() =>
        {
            S.OnboardingCompleted = false;
            _settings.Save();
            OnboardingStatus = "The setup assistant will open the next time WilcoATC starts.";
        });
    }

    // ------------------------------------------------------------------ immersion : copilote

    public bool CopilotEnabled { get => S.CopilotEnabled; set { S.CopilotEnabled = value; Raise(); } }
    /// <summary>Voix dédiée au copilote (null = voix par défaut du TTS).</summary>
    public string? CopilotVoiceName { get => S.CopilotVoiceName; set { S.CopilotVoiceName = value; Raise(); } }
    public bool CopilotChecklists { get => S.CopilotChecklists; set { S.CopilotChecklists = value; Raise(); } }

    /// <summary>Jeu d'annonces du copilote : déduit du gabarit, ou imposé (VFR / IFR).</summary>
    public Array CopilotRulesModes { get; } = Enum.GetValues(typeof(CopilotRulesMode));

    public CopilotRulesMode SelectedCopilotRules
    {
        get => S.CopilotRules;
        set { S.CopilotRules = value; Raise(); Raise(nameof(ShowCopilotVSpeeds)); }
    }

    /// <summary>
    /// Les vitesses V n'ont de sens qu'en IFR : en VFR le copilote ne les annonce pas
    /// (elles n'existent pas sur un avion léger). Inutile de les proposer alors.
    /// </summary>
    public bool ShowCopilotVSpeeds => S.CopilotRules != CopilotRulesMode.ForceVfr;
    public int CopilotV1Knots { get => S.CopilotV1Knots; set { S.CopilotV1Knots = value; Raise(); } }
    public int CopilotVrKnots { get => S.CopilotVrKnots; set { S.CopilotVrKnots = value; Raise(); } }
    public int CopilotV2Knots { get => S.CopilotV2Knots; set { S.CopilotV2Knots = value; Raise(); } }

    // ------------------------------------------------------------------ immersion : trafic ambiant

    public bool ChatterEnabled { get => S.ChatterEnabled; set { S.ChatterEnabled = value; Raise(); } }

    /// <summary>Le contrôle s'adresse au trafic RÉEL du simulateur (voir AppSettings).</summary>
    public bool TrafficAtcEnabled { get => S.TrafficAtcEnabled; set { S.TrafficAtcEnabled = value; Raise(); } }

    /// <summary>Faire naître du trafic piloté par le simulateur (voir AppSettings).</summary>
    public bool TrafficInjectionEnabled { get => S.TrafficInjectionEnabled; set { S.TrafficInjectionEnabled = value; Raise(); } }

    public int TrafficInjectionCount
    {
        get => S.TrafficInjectionCount;
        // Plafonné : chaque appareil injecté est un appareil que le simulateur doit calculer.
        set { S.TrafficInjectionCount = Math.Clamp(value, 0, 40); Raise(); }
    }
    public int ChatterMinGapSeconds
    {
        get => S.ChatterMinGapSeconds;
        set { S.ChatterMinGapSeconds = Math.Clamp(value, 5, 600); Raise(); }
    }
    public int ChatterMaxGapSeconds
    {
        get => S.ChatterMaxGapSeconds;
        set { S.ChatterMaxGapSeconds = Math.Clamp(value, 6, 900); Raise(); }
    }

    // ------------------------------------------------------------------ immersion : cabine

    public bool CabinEnabled { get => S.CabinEnabled; set { S.CabinEnabled = value; Raise(); } }
    public double CabinVolume { get => S.CabinVolume; set { S.CabinVolume = Math.Clamp(value, 0, 1); Raise(); } }
    public string CabinPacksDir => _cabinPacks.PacksDir;

    private IReadOnlyList<CabinSoundPack> _cabinPacksList = null!;
    public IReadOnlyList<CabinSoundPack> CabinPacks
    {
        get => _cabinPacksList ??= _cabinPacks.List();
        private set => SetProperty(ref _cabinPacksList, value);
    }

    public CabinSoundPack? SelectedCabinPack
    {
        get => CabinPacks.FirstOrDefault(p => p.Name.Equals(S.CabinPackName, StringComparison.OrdinalIgnoreCase))
               ?? CabinPacks.FirstOrDefault();
        set { S.CabinPackName = value?.Name; Raise(); }
    }

    public ICommand OpenCabinFolderCommand { get; }
    public ICommand RefreshCabinPacksCommand { get; }

    // ------------------------------------------------------------------ push-to-talk

    /// <summary>Levé par « Définir la touche » : la fenêtre capture la frappe suivante.</summary>
    public event Action? CapturePttRequested;

    public ICommand CapturePttCommand { get; private set; } = null!;
    public ICommand ClearPttCommand { get; private set; } = null!;

    public string PttKeyDisplay => S.PttVirtualKey == 0
        ? "—"
        : string.IsNullOrWhiteSpace(S.PttKeyName) ? S.PttVirtualKey.ToString() : S.PttKeyName;

    /// <summary>Appelé par la fenêtre quand l'utilisateur a pressé la touche à assigner.</summary>
    public void SetPttKey(Key key)
    {
        S.PttVirtualKey = GlobalPushToTalk.ToVirtualKey(key);
        S.PttKeyName = GlobalPushToTalk.DisplayName(key);
        _settings.Save();
        Raise(nameof(PttKeyDisplay));
    }

    // --- Variante joystick / HOTAS ---

    /// <summary>Levé par « Définir le bouton » : la fenêtre interroge le joystick jusqu'à un appui.</summary>
    public event Action? CaptureJoystickRequested;

    public ICommand CaptureJoystickCommand { get; private set; } = null!;
    public ICommand ClearJoystickCommand { get; private set; } = null!;

    private bool _pttJoystickCapturing;
    /// <summary>Vrai pendant « appuyez sur un bouton… » (affiche « … » et coupe le bouton).</summary>
    public bool PttJoystickCapturing
    {
        get => _pttJoystickCapturing;
        set { if (SetProperty(ref _pttJoystickCapturing, value)) Raise(nameof(PttJoystickDisplay)); }
    }

    public string PttJoystickDisplay
    {
        get
        {
            if (_pttJoystickCapturing) return "…";
            if (S.PttJoystickButton < 1) return "—";
            return string.IsNullOrWhiteSpace(S.PttJoystickName)
                ? $"Button {S.PttJoystickButton}"
                : $"{S.PttJoystickName} · Button {S.PttJoystickButton}";
        }
    }

    /// <summary>Appelé par la fenêtre quand un bouton de joystick a été capturé (ou l'abandon : button &lt; 1).</summary>
    public void SetPttJoystick(int device, int button)
    {
        if (button >= 1)
        {
            S.PttJoystickDevice = device;
            S.PttJoystickButton = button;
            S.PttJoystickName = GlobalJoystickButton.DeviceName(device);
            _settings.Save();
        }
        PttJoystickCapturing = false;
        Raise(nameof(PttJoystickDisplay));
    }

    private void OpenCabinFolder()
    {
        try
        {
            Directory.CreateDirectory(_cabinPacks.PacksDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_cabinPacks.PacksDir}\"") { UseShellExecute = true });
        }
        catch { /* on ignore */ }
    }

    /// <summary>Ouvre le dossier des échantillons radio, en le créant s'il n'existe pas encore.</summary>
    private void OpenRadioSamplesFolder()
    {
        try
        {
            Directory.CreateDirectory(_radioSamples.SamplesDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_radioSamples.SamplesDir}\"") { UseShellExecute = true });
        }
        catch { /* on ignore */ }
    }

    // ------------------------------------------------------------------ langue (interface + ATC)

    private IReadOnlyList<LanguageInfo> _availableLanguages = Loc.Available();
    public IReadOnlyList<LanguageInfo> AvailableLanguages
    {
        get => _availableLanguages;
        private set => SetProperty(ref _availableLanguages, value);
    }

    public LanguageInfo? SelectedAppLanguage
    {
        get => AvailableLanguages.FirstOrDefault(l => l.Code.Equals(S.AppLanguage, StringComparison.OrdinalIgnoreCase))
               ?? AvailableLanguages.FirstOrDefault();
        set
        {
            if (value is null || value.Code.Equals(S.AppLanguage, StringComparison.OrdinalIgnoreCase)) return;
            S.AppLanguage = value.Code;
            Loc.SetLanguage(value.Code); // change l'interface (et l'ATC) à chaud
            _settings.Save();            // la langue est persistée immédiatement
            Raise();
        }
    }

    public ICommand OpenLanguagesFolderCommand { get; }
    public ICommand RefreshLanguagesCommand { get; }

    // ------------------------------------------------------------------ diagnostic

    public ICommand OpenLogsFolderCommand { get; private set; } = null!;

    /// <summary>Chemin du journal de la session en cours (affiché sous le bouton).</summary>
    public string LogFilePath => FileLog.CurrentPath ?? FileLog.Directory;

    private void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(FileLog.Directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{FileLog.Directory}\"") { UseShellExecute = true });
        }
        catch { /* on ignore */ }
    }

    private void OpenLanguagesFolder()
    {
        try
        {
            Directory.CreateDirectory(Loc.LangDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Loc.LangDir}\"") { UseShellExecute = true });
        }
        catch { /* on ignore */ }
    }

    // ------------------------------------------------------------------ reconnaissance vocale (STT)

    public IReadOnlyList<AudioDevice> InputDevices { get; }

    private AudioDevice _selectedInputDevice;
    public AudioDevice SelectedInputDevice
    {
        get => _selectedInputDevice;
        set { if (SetProperty(ref _selectedInputDevice, value)) S.InputDeviceNumber = value?.Number ?? -1; }
    }

    public ICommand DownloadSpeechModelCommand { get; private set; } = null!;

    public string SpeechModelsDir => _whisper.ModelsDir;
    public string SpeechModelUrl => SpeechModelRepository.DefaultModelUrl;

    /// <summary>
    /// État du modèle de reconnaissance. Le cas « installé mais ANGLAIS SEUL » mérite son
    /// propre message : tout marche, l'utilisateur n'a aucune raison de soupçonner un
    /// problème — et pourtant le contrôleur ne comprendra pas un mot de français.
    /// </summary>
    public string SpeechModelStatus => !_whisper.IsInstalled
        ? Loc.T("S.Mic.StatusMissing")
        : _whisper.IsMultilingual
            ? Loc.T("S.Mic.StatusInstalled")
            : Loc.T("S.Mic.StatusEnglishOnly");

    /// <summary>Rafraîchit l'état du modèle STT après un téléchargement.</summary>
    public void RefreshSpeechModel() => Raise(nameof(SpeechModelStatus));

    // ------------------------------------------------------------------ Plan de vol / SimBrief

    public ICommand ImportSimBriefCommand { get; private set; } = null!;
    public ICommand LoadOfpCommand { get; private set; } = null!;

    public string SimBriefUsername
    {
        get => S.SimBriefUsername;
        set { S.SimBriefUsername = value; Raise(); }
    }

    private string _importStatus = "No flight plan imported.";
    public string ImportStatus { get => _importStatus; private set => SetProperty(ref _importStatus, value); }

    private async Task ImportSimBriefAsync()
    {
        ImportStatus = "Importing from SimBrief…";
        _settings.Save(); // on persiste le username saisi
        ImportStatus = await _importer.ImportFromSimBriefAsync(S.SimBriefUsername);
    }

    /// <summary>Appelé par la fenêtre après choix d'un fichier OFP XML.</summary>
    public void ImportOfpFile(string path) => ImportStatus = _importer.ImportFromOfpFile(path);

    // ------------------------------------------------------------------ test de voix

    /// <summary>Phrase d'essai : de la vraie phraséologie, pour juger la voix sur son usage réel.</summary>
    private const string TestPhrase =
        "Wilco Air one two three, radio check, reading you five by five.";

    private bool _testingVoice;

    private string _voiceTestStatus = "";
    /// <summary>Résultat du dernier test de voix — succès OU cause de l'échec.</summary>
    public string VoiceTestStatus
    {
        get => _voiceTestStatus;
        private set { _voiceTestStatus = value; Raise(); }
    }

    /// <summary>
    /// Joue une phrase d'essai avec le moteur, la voix et le périphérique ACTUELLEMENT
    /// sélectionnés. Réécrit intégralement : l'ancien bouton appelait
    /// <c>_atc.TriggerManualTest()</c>, qui souffrait de quatre défauts cumulés —
    ///
    ///  1. il n'utilisait PAS la voix choisie dans les réglages, mais celle dérivée d'un
    ///     hachage de la station courante (VoicePicker) ;
    ///  2. il abandonnait en silence si le canal voix était occupé (<c>if (IsBusy) return;</c>) ;
    ///  3. ses exceptions partaient dans le bandeau de la fenêtre PRINCIPALE, invisible
    ///     depuis la boîte de réglages -> « le bouton ne fait rien » ;
    ///  4. il n'enregistrait pas les réglages avant de synthétiser, donc le moteur pouvait
    ///     lire une valeur périmée.
    ///
    /// Ici chaque issue produit un message VISIBLE, et tout est journalisé.
    /// </summary>
    private async Task TestVoiceAsync()
    {
        if (_testingVoice) return;          // double-clic : on ne superpose pas deux essais
        _testingVoice = true;

        try
        {
            // (4) Les moteurs lisent les réglages à la synthèse : on persiste d'abord.
            _settings.Save();
            VoiceTestStatus = "Synthesising…";

            // (1) La voix EXPLICITEMENT choisie. Le nom ne concerne que sherpa-onnx ; les
            //     autres moteurs ont leur propre réglage et ignorent ce champ.
            var voice = S.TtsEngine == TtsEngineKind.Sherpa
                ? new TtsVoice(S.SherpaVoiceName, S.SherpaSpeakerId, (float)S.SherpaSpeed)
                : TtsVoice.Default;

            TtsAudio audio = await _tts.SynthesizeAsync(TestPhrase, voice);

            if (audio.IsEmpty)
            {
                VoiceTestStatus = S.TtsEngine == TtsEngineKind.Sherpa && string.IsNullOrWhiteSpace(S.SherpaVoiceName)
                    ? "No voice selected. Download one, then pick it above."
                    : "The engine returned no audio. Check that the selected voice is installed.";
                return;
            }

            // (2) On ATTEND le canal au lieu d'abandonner : un test qui ne dit rien est
            //     exactement le symptôme qu'on corrige.
            bool played = await _voice.SpeakAsync(audio, S.ToRadioProfile(), TimeSpan.FromSeconds(8));

            VoiceTestStatus = played
                ? $"Played on {SelectedDevice?.Name ?? "the default device"}."
                : "The radio channel stayed busy. Try again in a moment.";
        }
        catch (Exception ex)
        {
            // (3) L'erreur s'affiche ICI, dans la fenêtre où l'utilisateur a cliqué.
            VoiceTestStatus = "Voice test failed: " + ex.Message;
            FileLog.Exception("test de voix", ex);
        }
        finally { _testingVoice = false; }
    }

    // --- listes pour les combos ---
    public IReadOnlyList<AudioDevice> OutputDevices { get; }
    public ObservableCollection<string> Voices { get; } = new();
    public ObservableCollection<string> SherpaVoices { get; } = new();
    public IReadOnlyList<string> GoogleVoices { get; } = new GoogleCloudTtsEngine(() => ("", "")).GetVoices();
    public Array TtsEngines { get; } = Enum.GetValues(typeof(TtsEngineKind));
    public Array FlightRulesModes { get; } = Enum.GetValues(typeof(FlightRulesMode));

    public ICommand TestVoiceCommand { get; }
    public ICommand RestartOnboardingCommand { get; }

    private string _onboardingStatus = "";
    /// <summary>Confirmation affichée après « Relancer l'assistant ».</summary>
    public string OnboardingStatus
    {
        get => _onboardingStatus;
        private set { _onboardingStatus = value; Raise(); }
    }

    public ICommand DownloadVoiceCommand { get; }
    public ICommand DownloadAllVoicesCommand { get; private set; } = null!;

    /// <summary>Voix du catalogue pas encore installées (pour le téléchargement groupé).</summary>
    public IReadOnlyList<CatalogVoice> MissingVoices()
    {
        var installed = _voices.List().Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return CatalogVoices.Where(v => !installed.Contains(v.Name)).ToList();
    }

    /// <summary>Résumé « N voix à télécharger (~X Go) » pour le bouton.</summary>
    public string DownloadAllSummary
    {
        get
        {
            int n = MissingVoices().Count;
            // ~70 Mo par modèle en moyenne (64 Mo medium, 110-125 Mo high).
            double gb = n * 0.072;
            return n == 0 ? Loc.T("S.Voice.AllInstalled")
                          : string.Format(Loc.T("S.Voice.DownloadAllFmt"), n, gb);
        }
    }
    public ICommand OpenVoicesFolderCommand { get; }

    // --- sherpa-onnx (voix) ---
    public string VoicesDir => _voices.VoicesDir;

    public IReadOnlyList<CatalogVoice> CatalogVoices { get; } = VoiceCatalog.Voices;

    private CatalogVoice _selectedCatalogVoice;
    public CatalogVoice SelectedCatalogVoice
    {
        get => _selectedCatalogVoice;
        set => SetProperty(ref _selectedCatalogVoice, value);
    }

    public void RefreshSherpaVoices()
    {
        _voices.Invalidate();   // une voix vient peut-être d'être installée : on relit le disque
        SherpaVoices.Clear();
        foreach (var v in _voices.List()) SherpaVoices.Add(v.Name);
        Raise(nameof(SherpaVoiceName));
        // Une voix fraîchement installée peut débloquer une LANGUE entière : l'état affiché
        // sous le mode de langue doit suivre, sans quoi il annonce encore « manquant ».
        Raise(nameof(DownloadAllSummary));
    }

    public string? SherpaVoiceName
    {
        get => S.SherpaVoiceName;
        set { S.SherpaVoiceName = value; Raise(); }
    }

    public double SherpaSpeed
    {
        get => S.SherpaSpeed;
        set { S.SherpaSpeed = Math.Clamp(value, 0.5, 2.0); Raise(); }
    }

    private void OpenVoicesFolder()
    {
        try
        {
            Directory.CreateDirectory(_voices.VoicesDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_voices.VoicesDir}\"") { UseShellExecute = true });
        }
        catch { /* on ignore */ }
    }

    // --- audio de sortie ---
    private AudioDevice _selectedDevice;
    public AudioDevice SelectedDevice
    {
        get => _selectedDevice;
        set { if (SetProperty(ref _selectedDevice, value)) S.OutputDeviceNumber = value?.Number ?? -1; }
    }

    // --- TTS ---
    public TtsEngineKind SelectedTtsEngine
    {
        get => S.TtsEngine;
        set { S.TtsEngine = value; Raise(); }
    }

    public string? WindowsVoice
    {
        get => S.WindowsVoice;
        set { S.WindowsVoice = value; Raise(); }
    }

    public string GoogleVoiceName
    {
        get => S.GoogleVoiceName;
        set { S.GoogleVoiceName = value; Raise(); }
    }

    public string GoogleApiKeyEnvVar
    {
        get => S.GoogleApiKeyEnvVar;
        set { S.GoogleApiKeyEnvVar = value; Raise(); }
    }

    // --- ATC ---
    public bool AtcAutoContact
    {
        get => S.AtcAutoContact;
        set { S.AtcAutoContact = value; Raise(); }
    }

    /// <summary>
    /// Autoriser le repli sur les fréquences réelles quand le simulateur ne publie rien.
    /// Le simulateur reste prioritaire dans les deux cas.
    /// </summary>
    public bool UseRealWorldFrequencies
    {
        get => S.UseRealWorldFrequencies;
        set { S.UseRealWorldFrequencies = value; Raise(); Raise(nameof(ShowRealFrequencyWarning)); }
    }

    /// <summary>L'avertissement d'incomplétude ne s'affiche que quand l'option est active.</summary>
    public bool ShowRealFrequencyWarning => S.UseRealWorldFrequencies;

    /// <summary>
    /// Pause avant chaque transmission (ms). Elle recouvre la synthèse vocale : la baisser
    /// rend l'ATC plus vif, la monter lui donne un temps de réflexion.
    /// </summary>
    public double AtcResponseDelayMs
    {
        get => S.AtcResponseDelayMs;
        set { S.AtcResponseDelayMs = (int)Math.Clamp(value, 0, 3000); Raise(); }
    }

    /// <summary>Le contrôleur exige-t-il le collationnement de ses clairances ?</summary>
    public bool RequireReadback
    {
        get => S.RequireReadback;
        set { S.RequireReadback = value; Raise(); Raise(nameof(ShowRadioFailureOption)); }
    }

    /// <summary>Interception militaire après trois appels sans réponse.</summary>
    public bool ReadbackRadioFailureCall
    {
        get => S.ReadbackRadioFailureCall;
        set { S.ReadbackRadioFailureCall = value; Raise(); }
    }

    /// <summary>Sans collationnement exigé, il n'y a jamais d'appel sans réponse à escalader.</summary>
    public bool ShowRadioFailureOption => S.RequireReadback;

    /// <summary>Un chasseur vient-il escorter l'avion après l'annonce de panne radio ?</summary>
    public bool InterceptorEnabled
    {
        get => S.InterceptorEnabled;
        set { S.InterceptorEnabled = value; Raise(); }
    }

    /// <summary>
    /// Titre de conteneur de l'appareil. Vide = les titres connus du F/A-18E sont essayés
    /// l'un après l'autre ; le journal indique lequel a fonctionné.
    /// </summary>
    public string InterceptorTitle
    {
        get => S.InterceptorTitle;
        set { S.InterceptorTitle = value ?? ""; Raise(); }
    }

    public double InterceptorSeconds
    {
        get => S.InterceptorSeconds;
        set { S.InterceptorSeconds = (int)Math.Clamp(value, 15, 600); Raise(); }
    }

    public bool GsxIntegrationEnabled
    {
        get => S.GsxIntegrationEnabled;
        set { S.GsxIntegrationEnabled = value; Raise(); }
    }

    // --- ATIS ---
    public bool AtisEnabled
    {
        get => S.AtisEnabled;
        set { S.AtisEnabled = value; Raise(); }
    }

    public int AtisRepeatGapSeconds
    {
        get => S.AtisRepeatGapSeconds;
        set { S.AtisRepeatGapSeconds = Math.Clamp(value, 2, 120); Raise(); }
    }

    // Le Mode Test n'est plus exposé ici : l'écran CFG le pilote via
    // MainViewModel.TestModeEnabled, qui passe par l'AtcController — un seul état, donc le
    // bandeau orange de la fenêtre principale reste toujours d'accord avec l'interrupteur.

    // --- Secteurs en-route (fréquences Centre) ---

    /// <summary>
    /// État des secteurs ACC. Sans eux, l'étape « Centre » est sautée partout où les données
    /// aéroport n'en publient pas — c'est-à-dire hors d'Amérique du Nord — et le pilote reste
    /// avec le Départ pendant toute la croisière.
    /// </summary>
    public string EnrouteSectorsStatus => _sectors.IsInstalled
        ? $"{_sectors.Count} en-route sectors installed. {EnrouteSectorImporter.Attribution}"
        : "No en-route sector installed — outside North America the controller cannot hand you "
          + "over to a Center, because airport data publishes no Center frequency there.";

    private string _enrouteProgress = "";
    /// <summary>Progression de l'installation (téléchargement, lecture, écriture).</summary>
    public string EnrouteProgress { get => _enrouteProgress; private set => SetProperty(ref _enrouteProgress, value); }

    public ICommand DownloadEnrouteSectorsCommand { get; private set; } = null!;

    private bool _installingSectors;

    private async Task InstallEnrouteSectorsAsync()
    {
        if (_installingSectors) return;
        _installingSectors = true;
        try
        {
            var progress = new Progress<string>(s => EnrouteProgress = s);
            EnrouteProgress = await new EnrouteSectorImporter(_sectors).InstallAsync(progress);
            Raise(nameof(EnrouteSectorsStatus));
        }
        finally { _installingSectors = false; }
    }

    // --- Transferts / Centre ---
    public bool VatsimEnabled { get => S.VatsimEnabled; set { S.VatsimEnabled = value; Raise(); } }
    public double CenterFrequencyMhz { get => S.CenterFrequencyMhz; set { S.CenterFrequencyMhz = value; Raise(); } }
    public string CenterName { get => S.CenterName; set { S.CenterName = value; Raise(); } }

    // --- Règles de vol (VFR / IFR) ---
    public FlightRulesMode SelectedFlightRules
    {
        get => S.FlightRules;
        set { S.FlightRules = value; Raise(); }
    }

    // Le choix de la LANGUE DU CONTRÔLEUR a été retiré : il ne parle et ne comprend que
    // l'anglais pour l'instant (voir LanguageResolver.Effective). Le réglage reviendra avec
    // le multilingue — inutile d'exposer un sélecteur qui ne changerait rien.

    // Le « mode IA » (génération des phrases par un LLM local ou cloud) n'est plus réglable :
    // il n'y a plus d'onglet Avancé, et tout ce qui se règle vit maintenant dans l'écran CFG.
    // Le LLM a été entièrement supprimé du projet : l'ATC parle par gabarits, hors ligne,
    // sans clé et sans attendre personne.

    // --- effet radio ---
    public bool RadioBandPass { get => S.RadioBandPass; set { S.RadioBandPass = value; Raise(); } }
    public bool RadioSquelch { get => S.RadioSquelch; set { S.RadioSquelch = value; Raise(); } }
    public bool RadioSaturation { get => S.RadioSaturation; set { S.RadioSaturation = value; Raise(); } }
    public double RadioVolume { get => S.RadioVolume; set { S.RadioVolume = Math.Clamp(value, 0, 1); Raise(); } }
    public double RadioIntensity { get => S.RadioIntensity; set { S.RadioIntensity = Math.Clamp(value, 0, 1); Raise(); } }
    public double RadioBedVolume { get => S.RadioBedVolume; set { S.RadioBedVolume = Math.Clamp(value, 0, 1); Raise(); } }

    // --- Échantillons radio réels ---

    public string RadioSamplesDir => _radioSamples.SamplesDir;

    /// <summary>État du pack : combien de variantes par catégorie, et ce qui manque.</summary>
    public string RadioSamplesState
    {
        get
        {
            int keyup = _radioSamples.Count(RadioSampleKind.KeyUp);
            int breath = _radioSamples.Count(RadioSampleKind.Breath);
            int tail = _radioSamples.Count(RadioSampleKind.Tail);
            int bed = _radioSamples.Count(RadioSampleKind.Bed);

            if (keyup + breath + tail + bed == 0)
                return "No sample found — synthesised clicks are used, and no breath is played.";

            return $"keyup {keyup}   ·   breath {breath}   ·   tail {tail}   ·   bed {bed}";
        }
    }

    public ICommand OpenRadioSamplesFolderCommand { get; }
    public ICommand RefreshRadioSamplesCommand { get; }
}
