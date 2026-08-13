using WilcoATC.Atc;
using WilcoATC.Audio;
using WilcoATC.Settings;
using NAudio.Wave;
using SherpaOnnx;

namespace WilcoATC.Atc.Understanding;

/// <summary>
/// Reconnaissance vocale (STT) par défaut : capture micro (NAudio) + ASR offline
/// **100 % natif en C#** via sherpa-onnx. Deux familles de modèles sont gérées : les
/// transducteurs NeMo (Parakeet, le défaut) et Whisper (installations existantes).
///
/// Push-to-talk : <see cref="StartListening"/> ouvre le micro et accumule les échantillons ;
/// <see cref="StopAndTranscribeAsync"/> ferme le micro et renvoie le texte transcrit. Aucun
/// modèle installé -> indisponible (la saisie texte reste toujours utilisable, donc le STT
/// n'est jamais requis).
/// </summary>
public sealed class SherpaSpeechToText : ISpeechToText, IDisposable
{
    private const int SampleRate = 16000; // les modèles attendent du 16 kHz mono.
    private const int BytesPerSample = 2; // PCM 16 bits

    /// <summary>
    /// Temps de capture CONSERVÉ après le relâchement de la touche. On relâche presque
    /// toujours sur la dernière syllabe : sans ce tampon, « ready for departure » perd son
    /// « -ture » et le mot-clé ne matche plus. 250 ms suffisent et ne se sentent pas.
    /// </summary>
    private const int TailMilliseconds = 250;

    /// <summary>
    /// Durée minimale d'un enregistrement exploitable. ATTENTION à l'unité : le seuil
    /// précédent comparait <c>pcm.Length</c> (des OCTETS) à <c>SampleRate</c>, ce qui faisait
    /// 0,5 s réelle alors que le commentaire annonçait 0,3 s — et jetait donc en SILENCE les
    /// transmissions brèves mais parfaitement valides (« wilco », « roger », « go ahead »).
    /// </summary>
    private const double MinSeconds = 0.25;

    private readonly SpeechModelRepository _models;
    private readonly SettingsService _settings;
    private readonly Func<AtcLanguage> _language;

    private readonly object _gate = new();
    private OfflineRecognizer? _recognizer;
    private string? _loadedKey; // moteur + encodeur : recharge si l'un change

    private WaveInEvent? _capture;
    private readonly List<byte> _buffer = new();
    private TaskCompletionSource<bool>? _stopped;

    public SherpaSpeechToText(SpeechModelRepository models, SettingsService settings, Func<AtcLanguage> language)
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

        // On laisse le micro OUVERT un court instant avant de couper : la fin de phrase
        // tombe presque toujours après le relâchement de la touche.
        var trip = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(TailMilliseconds, ct).ConfigureAwait(false);

        capture.StopRecording();
        if (stopped is not null) await stopped.Task.ConfigureAwait(false); // attend le vidage du buffer
        long tClosed = trip.ElapsedMilliseconds;

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

        // Trop court pour contenir de la parole -> rien à transcrire.
        if (pcm.Length < MinSeconds * SampleRate * BytesPerSample) return string.Empty;

        string result = await Task.Run(() => TranscribePcm16(pcm), ct).ConfigureAwait(false);

        // Fermeture du micro et décodage : l'autre moitié de la latence ressentie, celle que
        // le contrôleur ne voit pas. Le premier décodage d'une session charge le modèle
        // (~1,5 s) ; les suivants tournent autour de 150 ms. Si ce n'est pas le cas ici,
        // c'est le pilote audio qui traîne à rendre la main, pas la reconnaissance.
        Diagnostics.FileLog.Write(
            $"[latence] micro fermé {tClosed} ms · décodage {trip.ElapsedMilliseconds - tClosed} ms " +
            $"({pcm.Length / (double)(SampleRate * BytesPerSample):F1} s d'audio)");

        return result;
    }

    /// <summary>
    /// Transcrit un tampon PCM 16 bits mono 16 kHz. Public pour pouvoir vérifier le moteur
    /// sans micro (bancs d'essai) : c'est exactement le chemin utilisé par le push-to-talk.
    /// </summary>
    public string TranscribePcm16(byte[] pcm16)
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

        Condition(samples);

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
            System.Diagnostics.Debug.WriteLine("[WilcoATC/STT] transcription : " + ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// Conditionne le signal AVANT Whisper. Deux corrections, dans cet ordre :
    ///
    ///  1. RETRAIT DE LA COMPOSANTE CONTINUE : beaucoup de casques USB sortent un signal
    ///     légèrement décalé de zéro ; ce décalage ne s'entend pas mais fausse l'analyse
    ///     spectrale et la normalisation qui suit.
    ///  2. NORMALISATION EN CRÊTE : c'est la correction qui compte. Whisper est entraîné
    ///     sur de l'audio à niveau « normal » ; un micro réglé bas (cas courant sur un
    ///     casque de simu, où le gain est baissé pour ne pas saturer) produit des
    ///     transcriptions bien pires. On remonte la crête à -3 dBFS, avec un plafond de
    ///     gain ×12 pour ne pas amplifier un silence en soufflerie.
    ///
    /// Modifie le tableau EN PLACE (il est temporaire et à nous seuls).
    /// </summary>
    public static void Condition(float[] samples)
    {
        if (samples.Length == 0) return;

        double sum = 0;
        foreach (float s in samples) sum += s;
        float dc = (float)(sum / samples.Length);

        float peak = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] -= dc;
            float a = Math.Abs(samples[i]);
            if (a > peak) peak = a;
        }

        // Trop faible pour être de la parole -> on ne remonte pas du bruit de fond.
        if (peak < 0.005f) return;

        const float target = 0.7f;   // ≈ -3 dBFS
        float gain = Math.Min(target / peak, 12f);
        if (gain <= 1.05f) return;   // déjà à un bon niveau

        for (int i = 0; i < samples.Length; i++)
            samples[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
    }

    private OfflineRecognizer EnsureLoaded(SpeechModel model)
    {
        lock (_gate)
        {
            string key = model.Engine + "|" + model.EncoderPath;
            if (_recognizer is not null && _loadedKey == key) return _recognizer;

            _recognizer?.Dispose();

            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Tokens = model.TokensPath;
            // Le décodage tourne hors thread UI : plusieurs cœurs pour que la transcription
            // revienne vite, même avec un modèle plus gros.
            config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
            config.ModelConfig.Provider = "cpu";
            config.ModelConfig.Debug = 0;
            config.DecodingMethod = "greedy_search";

            if (model.Engine == SpeechEngine.NemoTransducer)
            {
                // Parakeet & co. : encodeur / décodeur / joiner. ModelType est OBLIGATOIRE,
                // sinon sherpa tente de deviner et échoue au chargement.
                config.ModelConfig.Transducer.Encoder = model.EncoderPath;
                config.ModelConfig.Transducer.Decoder = model.DecoderPath;
                config.ModelConfig.Transducer.Joiner = model.JoinerPath;
                config.ModelConfig.ModelType = "nemo_transducer";
            }
            else
            {
                config.ModelConfig.Whisper.Encoder = model.EncoderPath;
                config.ModelConfig.Whisper.Decoder = model.DecoderPath;
                // Anglais IMPOSÉ : l'ATC ne comprend que cette langue pour l'instant, et le
                // dire au modèle vaut mieux que le laisser deviner — une détection qui se
                // trompe rendrait un texte dans une langue dont aucun mot-clé n'existe.
                config.ModelConfig.Whisper.Language = "en";
                config.ModelConfig.Whisper.Task = "transcribe";
            }

            _recognizer = new OfflineRecognizer(config);
            _loadedKey = key;
            return _recognizer;
        }
    }

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
