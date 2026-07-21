using FreqWatch.Settings;

namespace FreqWatch.Audio;

/// <summary>
/// Choisit le moteur TTS selon les réglages, à chaque synthèse.
/// Défaut = **sherpa-onnx** (Piper natif). Si le moteur choisi échoue (voix non
/// installée, erreur), on retombe automatiquement sur la voix Windows (SAPI) :
/// la boucle vocale ne casse jamais.
/// </summary>
public sealed class TtsEngineSelector : ITtsEngine, IDisposable
{
    private readonly SettingsService _settings;
    private readonly WindowsTtsEngine _windows;
    private readonly SherpaOnnxTtsEngine _sherpa;
    private readonly GoogleCloudTtsEngine _google;

    public TtsEngineSelector(SettingsService settings, VoiceRepository voices)
    {
        _settings = settings;
        _windows = new WindowsTtsEngine(() => settings.Current.WindowsVoice);
        _sherpa = new SherpaOnnxTtsEngine(voices,
            () => (settings.Current.SherpaVoiceName, (float)settings.Current.SherpaSpeed, settings.Current.SherpaSpeakerId));
        _google = new GoogleCloudTtsEngine(() => (settings.Current.GoogleApiKeyEnvVar, settings.Current.GoogleVoiceName));
    }

    public IReadOnlyList<string> GetVoices() => _settings.Current.TtsEngine switch
    {
        TtsEngineKind.Sherpa => _sherpa.GetVoices(),
        TtsEngineKind.Google => _google.GetVoices(),
        _ => _windows.GetVoices(),
    };

    public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken ct = default)
        => SynthesizeAsync(text, TtsVoice.Default, ct);

    public async Task<TtsAudio> SynthesizeAsync(string text, TtsVoice voice, CancellationToken ct = default)
    {
        ITtsEngine? preferred = _settings.Current.TtsEngine switch
        {
            TtsEngineKind.Sherpa => _sherpa,
            TtsEngineKind.Google => _google,
            _ => null, // Windows : moteur direct ci-dessous
        };

        if (preferred is not null)
        {
            try
            {
                var audio = await preferred.SynthesizeAsync(text, voice, ct).ConfigureAwait(false);
                if (!audio.IsEmpty) return audio;
            }
            catch { /* repli Windows ci-dessous */ }
        }

        return await _windows.SynthesizeAsync(text, ct).ConfigureAwait(false);
    }

    public void Dispose() => _sherpa.Dispose();
}
