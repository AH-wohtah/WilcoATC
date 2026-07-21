using System.IO;
using System.Speech.Synthesis;

namespace FreqWatch.Audio;

/// <summary>
/// TTS par défaut, via System.Speech (SAPI 5 / voix « Desktop » de Windows).
/// Fonctionne SANS rien installer -> garantit la boucle vocale hors-ligne et gratuite.
///
/// On synthétise vers un flux WAV en mémoire (SetOutputToWaveStream), puis on lit
/// le PCM avec NAudio et on le ramène en mono float pour le pipeline radio.
/// </summary>
public sealed class WindowsTtsEngine : ITtsEngine
{
    private readonly Func<string?> _voiceProvider;

    public WindowsTtsEngine(Func<string?> voiceProvider) => _voiceProvider = voiceProvider;

    public IReadOnlyList<string> GetVoices()
    {
        try
        {
            using var s = new SpeechSynthesizer();
            return s.GetInstalledVoices()
                    .Where(v => v.Enabled)
                    .Select(v => v.VoiceInfo.Name)
                    .ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    public Task<TtsAudio> SynthesizeAsync(string text, CancellationToken ct = default)
        => Task.Run(() => Synthesize(text), ct);

    private TtsAudio Synthesize(string text)
    {
        using var synth = new SpeechSynthesizer();

        string? voice = _voiceProvider();
        if (!string.IsNullOrWhiteSpace(voice))
        {
            try { synth.SelectVoice(voice); }
            catch { /* voix indisponible -> voix par défaut */ }
        }

        using var ms = new MemoryStream();
        synth.SetOutputToWaveStream(ms);
        synth.Speak(text);
        // Détache la sortie : finalise l'en-tête WAV (tailles de chunks) dans le flux,
        // sinon WaveFileReader pourrait lire une taille de données nulle.
        synth.SetOutputToNull();
        ms.Position = 0;
        return WavUtil.ReadMono(ms);
    }
}
