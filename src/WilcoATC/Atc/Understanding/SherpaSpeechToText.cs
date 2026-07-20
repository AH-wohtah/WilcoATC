using FreqWatch.Atc;
using FreqWatch.Audio;
using FreqWatch.Settings;
using NAudio.Wave;
using SherpaOnnx;

namespace FreqWatch.Atc.Understanding;

/// <summary>
/// Reconnaissance vocale (STT) par défaut : capture micro (NAudio) + ASR offline
/// **100 % natif en C#** via sherpa-onnx (modèle Whisper MULTILINGUE, FR + EN).
///
/// Push-to-talk : <see cref="StartListening"/> ouvre le micro et accumule les échantillons ;
/// <see cref="StopAndTranscribeAsync"/> ferme le micro et renvoie le texte transcrit. La
/// langue Whisper suit la langue ATC effective (FR/EN). Aucun modèle installé -> indisponible
/// (la saisie texte reste toujours utilisable, donc le STT n'est jamais requis).
/// </summary>
public sealed class SherpaSpeechToText : ISpeechToText, IDisposable
{
    private const int SampleRate = 16000; // Whisper attend du 16 kHz mono.

    private readonly WhisperModelRepository _models;
    private readonly SettingsService _settings;
    private readonly Func<AtcLanguage> _language;

    private readonly object _gate = new();
    private OfflineRecognizer? _recognizer;
    private string? _loadedKey; // encodeur + langue : recharge si l'un change

    private WaveInEvent? _capture;
    private readonly List<byte> _buffer = new();
    private TaskCompletionSource<bool>? _stopped;

    public SherpaSpeechToText(WhisperModelRepository models, SettingsService settings, Func<AtcLanguage> language)
    {
        _models = models;
        _settings = settings;
        _language = language;
    }

    public bool IsAvailable => _models.IsInstalled;

    public void StartListening()
    {
        lock (_gate)
        {
            if (_capture is not null) return; // déjà en écoute
            _buffer.Clear();
            _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var capture = new WaveInEvent
            {
                DeviceNumber = _settings.Current.InputDeviceNumber,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 50,
            };
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            _capture = capture;
            capture.StartRecording();
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (_capture is null) return;
            for (int i = 0; i < e.BytesRecorded; i++) _buffer.Add(e.Buffer[i]);
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e) => _stopped?.TrySetResult(true);

    public async Task<string> StopAndTranscribeAsync(CancellationToken ct = default)
    {
        WaveInEvent? capture;
        TaskCompletionSource<bool>? stopped;
        lock (_gate) { capture = _capture; stopped = _stopped; }
        if (capture is null) return string.Empty;

        capture.StopRecording();
        if (stopped is not null) await stopped.Task.ConfigureAwait(false); // attend le vidage du buffer

        byte[] pcm;
        lock (_gate)
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
            capture.Dispose();
            _capture = null;
            _stopped = null;
            pcm = _buffer.ToArray();
            _buffer.Clear();
        }

        // Trop court (< ~0.3 s en 16-bit = 2 octets/échantillon) -> rien à transcrire.
        if (pcm.Length < SampleRate) return string.Empty;

        return await Task.Run(() => Transcribe(pcm), ct).ConfigureAwait(false);
    }

    private string Transcribe(byte[] pcm16)
    {
        var model = _models.Resolve();
        if (model is null) return string.Empty;

        // PCM 16-bit little-endian -> float [-1, 1].
        int n = pcm16.Length / 2;
        float[] samples = new float[n];
        for (int i = 0; i < n; i++)
        {
            short s = (short)(pcm16[2 * i] | (pcm16[2 * i + 1] << 8));
            samples[i] = s / 32768f;
        }

        try
        {
            var recognizer = EnsureLoaded(model);
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(SampleRate, samples);
            recognizer.Decode(stream);
            return stream.Result.Text?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[FreqWatch/STT] transcription : " + ex);
            return string.Empty;
        }
    }

    private OfflineRecognizer EnsureLoaded(WhisperModel model)
    {
        lock (_gate)
        {
            string lang = WhisperLang();
            string key = model.EncoderPath + "|" + lang;
            if (_recognizer is not null && _loadedKey == key) return _recognizer;

            _recognizer?.Dispose();

            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Whisper.Encoder = model.EncoderPath;
            config.ModelConfig.Whisper.Decoder = model.DecoderPath;
            config.ModelConfig.Whisper.Language = lang;      // "fr" / "en" (modèle multilingue)
            config.ModelConfig.Whisper.Task = "transcribe";
            config.ModelConfig.Tokens = model.TokensPath;
            config.ModelConfig.NumThreads = 1;
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.Debug = 0;
            config.DecodingMethod = "greedy_search";

            _recognizer = new OfflineRecognizer(config);
            _loadedKey = key;
            return _recognizer;
        }
    }

    private string WhisperLang() => _language() == AtcLanguage.French ? "fr" : "en";

    public void Dispose()
    {
        lock (_gate)
        {
            try { _capture?.Dispose(); } catch { }
            _capture = null;
            _recognizer?.Dispose();
            _recognizer = null;
            _loadedKey = null;
        }
    }
}
