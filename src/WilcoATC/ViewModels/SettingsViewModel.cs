using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using FreqWatch.Atc;
using FreqWatch.Atc.Planning;
using FreqWatch.Audio;
using FreqWatch.Localization;
using FreqWatch.Settings;

namespace FreqWatch.ViewModels;

/// <summary>
/// ViewModel des réglages. Les setters écrivent directement dans
/// <see cref="SettingsService.Current"/> (appliqué à la volée), et
/// <see cref="SaveCommand"/> persiste sur disque.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IAtcController _atc;
    private readonly VoiceRepository _voices;
    private readonly FlightPlanImporter _importer;
    private readonly WhisperModelRepository _whisper;

    private AppSettings S => _settings.Current;

    /// <summary>Levé par le bouton « Télécharger cette voix » (l'ouverture est gérée côté fenêtre).</summary>
    public event Action<CatalogVoice>? DownloadVoiceRequested;

    /// <summary>Levé par le bouton « Charger un OFP » (dialogue fichier géré côté fenêtre).</summary>
    public event Action? LoadOfpRequested;

    /// <summary>Levé par le bouton « Télécharger le modèle vocal » (progression gérée côté fenêtre).</summary>
    public event Action? DownloadSpeechModelRequested;

    public SettingsViewModel(SettingsService settings, IAtcController atc, VoiceRepository voices,
                             FlightPlanImporter importer, WhisperModelRepository whisper)
    {
        _settings = settings;
        _atc = atc;
        _voices = voices;
        _importer = importer;
        _whisper = whisper;

        OutputDevices = AudioDeviceService.GetOutputDevices();
        _selectedDevice = OutputDevices.FirstOrDefault(d => d.Number == S.OutputDeviceNumber) ?? OutputDevices[0];
        InputDevices = AudioDeviceService.GetInputDevices();
        _selectedInputDevice = InputDevices.FirstOrDefault(d => d.Number == S.InputDeviceNumber) ?? InputDevices[0];

        foreach (var v in new WindowsTtsEngine(() => null).GetVoices())
            Voices.Add(v);
        RefreshSherpaVoices();

        _selectedCatalogVoice = CatalogVoices[0];

        TestVoiceCommand = new RelayCommand(() => _atc.TriggerManualTest());
        SaveCommand = new RelayCommand(() => _settings.Save());
        DownloadVoiceCommand = new RelayCommand(() => DownloadVoiceRequested?.Invoke(SelectedCatalogVoice));
        OpenVoicesFolderCommand = new RelayCommand(OpenVoicesFolder);
        ImportSimBriefCommand = new RelayCommand(() => _ = ImportSimBriefAsync());
        LoadOfpCommand = new RelayCommand(() => LoadOfpRequested?.Invoke());
        DownloadSpeechModelCommand = new RelayCommand(() => DownloadSpeechModelRequested?.Invoke());
        OpenLanguagesFolderCommand = new RelayCommand(OpenLanguagesFolder);
        RefreshLanguagesCommand = new RelayCommand(() => { AvailableLanguages = Loc.Available(); Raise(nameof(SelectedAppLanguage)); });
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
    public string SpeechModelUrl => WhisperModelRepository.DefaultModelUrl;

    public string SpeechModelStatus => _whisper.IsInstalled
        ? Loc.T("S.Mic.StatusInstalled")
        : Loc.T("S.Mic.StatusMissing");

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

    private string _importStatus = "Aucun plan importé.";
    public string ImportStatus { get => _importStatus; private set => SetProperty(ref _importStatus, value); }

    private async Task ImportSimBriefAsync()
    {
        ImportStatus = "Import SimBrief en cours…";
        _settings.Save(); // on persiste le username saisi
        ImportStatus = await _importer.ImportFromSimBriefAsync(S.SimBriefUsername);
    }

    /// <summary>Appelé par la fenêtre après choix d'un fichier OFP XML.</summary>
    public void ImportOfpFile(string path) => ImportStatus = _importer.ImportFromOfpFile(path);

    // --- listes pour les combos ---
    public IReadOnlyList<AudioDevice> OutputDevices { get; }
    public ObservableCollection<string> Voices { get; } = new();
    public ObservableCollection<string> SherpaVoices { get; } = new();
    public IReadOnlyList<string> GoogleVoices { get; } = new GoogleCloudTtsEngine(() => ("", "")).GetVoices();
    public Array TtsEngines { get; } = Enum.GetValues(typeof(TtsEngineKind));
    public Array LlmModes { get; } = Enum.GetValues(typeof(LlmMode));

    public ICommand TestVoiceCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DownloadVoiceCommand { get; }
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
        SherpaVoices.Clear();
        foreach (var v in _voices.List()) SherpaVoices.Add(v.Name);
        Raise(nameof(SherpaVoiceName));
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

    public bool GsxIntegrationEnabled
    {
        get => S.GsxIntegrationEnabled;
        set { S.GsxIntegrationEnabled = value; Raise(); }
    }

    // --- Mode Test (débogage) ---
    public bool TestMode
    {
        get => S.TestMode;
        set { S.TestMode = value; Raise(); }
    }

    // --- Transferts / Centre ---
    public bool VatsimEnabled { get => S.VatsimEnabled; set { S.VatsimEnabled = value; Raise(); } }
    public double CenterFrequencyMhz { get => S.CenterFrequencyMhz; set { S.CenterFrequencyMhz = value; Raise(); } }
    public string CenterName { get => S.CenterName; set { S.CenterName = value; Raise(); } }

    // --- LLM (optionnel) ---
    public LlmMode SelectedLlmMode
    {
        get => S.Llm;
        set { S.Llm = value; Raise(); }
    }

    public string OllamaUrl { get => S.OllamaUrl; set { S.OllamaUrl = value; Raise(); } }
    public string OllamaModel { get => S.OllamaModel; set { S.OllamaModel = value; Raise(); } }
    public string CloudBaseUrl { get => S.CloudBaseUrl; set { S.CloudBaseUrl = value; Raise(); } }
    public string CloudModel { get => S.CloudModel; set { S.CloudModel = value; Raise(); } }
    public string CloudApiKeyEnvVar { get => S.CloudApiKeyEnvVar; set { S.CloudApiKeyEnvVar = value; Raise(); } }

    // --- effet radio ---
    public bool RadioBandPass { get => S.RadioBandPass; set { S.RadioBandPass = value; Raise(); } }
    public bool RadioHiss { get => S.RadioHiss; set { S.RadioHiss = value; Raise(); } }
    public bool RadioSquelch { get => S.RadioSquelch; set { S.RadioSquelch = value; Raise(); } }
    public bool RadioSaturation { get => S.RadioSaturation; set { S.RadioSaturation = value; Raise(); } }
    public double RadioVolume { get => S.RadioVolume; set { S.RadioVolume = Math.Clamp(value, 0, 1); Raise(); } }
}
