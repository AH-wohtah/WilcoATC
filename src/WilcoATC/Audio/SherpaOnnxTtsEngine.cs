using SherpaOnnx;

namespace FreqWatch.Audio;

/// <summary>
/// Moteur TTS par défaut : Piper/VITS **100 % natif en C#** via sherpa-onnx.
/// Aucun Python, aucun exe externe, aucune clé API — tout tourne dans le process
/// .NET et hors-ligne une fois la voix installée.
///
/// La génération produit le **PCM en mémoire** (<c>OfflineTts.Generate</c> ->
/// <c>Samples</c> float[] + <c>SampleRate</c>) qu'on renvoie tel quel via
/// <see cref="ITtsEngine"/> pour alimenter le pipeline radio existant.
/// </summary>
public sealed class SherpaOnnxTtsEngine : ITtsEngine, IDisposable
{
    private readonly VoiceRepository _voices;
    private readonly Func<(string? VoiceName, float Speed, int SpeakerId)> _config;

    private readonly object _gate = new();
    private OfflineTts? _tts;
    private string? _loadedOnnxPath; // pour recharger si l'on change de voix

    public SherpaOnnxTtsEngine(VoiceRepository voices, Func<(string?, float, int)> config)
    {
        _voices = voices;
        _config = config;
    }

    public IReadOnlyList<string> GetVoices()
        => _voices.List().Select(v => v.Name).ToList();

    public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken ct = default)
        => Task.Run(() => Synthesize(text), ct);

    private TtsAudio Synthesize(string text)
    {
        var (voiceName, speed, speakerId) = _config();

        var voice = _voices.Resolve(voiceName);
        if (voice is null)
            return TtsAudio.Empty; // aucune voix installée -> le sélecteur retombe sur Windows

        OfflineTts tts = EnsureLoaded(voice);
        var generated = tts.Generate(text, speed <= 0 ? 1.0f : speed, speakerId);

        // Samples : float PCM [-1, 1] en mémoire ; SampleRate : ex. 22050.
        return new TtsAudio(generated.Samples, generated.SampleRate, 1);
    }

    // Charge (et met en cache) l'OfflineTts pour la voix courante ; recharge si elle change.
    private OfflineTts EnsureLoaded(VoiceModel voice)
    {
        lock (_gate)
        {
            if (_tts is not null && _loadedOnnxPath == voice.OnnxPath) return _tts;

            _tts?.Dispose();

            var config = new OfflineTtsConfig();
            config.Model.Vits.Model = voice.OnnxPath;
            config.Model.Vits.Tokens = voice.TokensPath;
            config.Model.Vits.DataDir = voice.DataDir; // espeak-ng-data
            config.Model.NumThreads = 1;
            config.Model.Provider = "cpu";
            config.MaxNumSentences = 1;

            _tts = new OfflineTts(config);
            _loadedOnnxPath = voice.OnnxPath;
            return _tts;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tts?.Dispose();
            _tts = null;
            _loadedOnnxPath = null;
        }
    }
}
