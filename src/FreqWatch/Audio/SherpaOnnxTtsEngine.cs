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
    // Plusieurs voix peuvent être actives EN MÊME TEMPS (ATC, copilote, équipages
    // d'ambiance) : on garde donc un modèle chargé par voix, au lieu d'un seul.
    private readonly Dictionary<string, OfflineTts> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public SherpaOnnxTtsEngine(VoiceRepository voices, Func<(string?, float, int)> config)
    {
        _voices = voices;
        _config = config;
    }

    public IReadOnlyList<string> GetVoices()
        => _voices.List().Select(v => v.Name).ToList();

    public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken ct = default)
        => Task.Run(() => Synthesize(text, TtsVoice.Default), ct);

    public Task<TtsAudio> SynthesizeAsync(string text, TtsVoice voice, CancellationToken ct = default)
        => Task.Run(() => Synthesize(text, voice), ct);

    private TtsAudio Synthesize(string text, TtsVoice requested)
    {
        var (defaultVoiceName, defaultSpeed, defaultSpeaker) = _config();

        // Voix imposée si fournie, sinon celle des réglages.
        var voice = _voices.Resolve(requested.Name ?? defaultVoiceName);
        if (voice is null)
            return TtsAudio.Empty; // aucune voix installée -> le sélecteur retombe sur Windows

        OfflineTts tts = EnsureLoaded(voice);

        float speed = (defaultSpeed <= 0 ? 1.0f : defaultSpeed)
                      * (requested.SpeedScale <= 0 ? 1f : requested.SpeedScale);
        speed = Math.Clamp(speed, 0.5f, 2.0f);

        // Le locuteur n'existe que sur les modèles multi-locuteurs : on borne.
        int speakerId = requested.Name is null && requested.SpeakerId == 0 ? defaultSpeaker : requested.SpeakerId;
        int speakers = Math.Max(1, tts.NumSpeakers);
        speakerId = Math.Abs(speakerId) % speakers;

        var generated = tts.Generate(text, speed, speakerId);

        // Samples : float PCM [-1, 1] en mémoire ; SampleRate : ex. 22050.
        return new TtsAudio(generated.Samples, generated.SampleRate, 1);
    }

    // Charge (et met en cache) un OfflineTts PAR VOIX.
    private OfflineTts EnsureLoaded(VoiceModel voice)
    {
        lock (_gate)
        {
            if (_loaded.TryGetValue(voice.OnnxPath, out var cached)) return cached;

            var config = new OfflineTtsConfig();
            config.Model.Vits.Model = voice.OnnxPath;
            config.Model.Vits.Tokens = voice.TokensPath;
            config.Model.Vits.DataDir = voice.DataDir; // espeak-ng-data
            config.Model.NumThreads = 1;
            config.Model.Provider = "cpu";
            config.MaxNumSentences = 1;

            var tts = new OfflineTts(config);
            _loaded[voice.OnnxPath] = tts;
            return tts;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var tts in _loaded.Values)
            {
                try { tts.Dispose(); } catch { }
            }
            _loaded.Clear();
        }
    }
}
