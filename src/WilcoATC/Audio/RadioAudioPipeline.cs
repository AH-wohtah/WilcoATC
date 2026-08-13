using System.IO;
using NAudio.Wave;

namespace WilcoATC.Audio;

/// <summary>
/// Applique l'effet radio (voir <see cref="RadioDsp"/>) au PCM de la TTS, puis JOUE
/// le résultat sur le périphérique de sortie choisi via NAudio (WaveOutEvent).
///
/// NB : on joue le son « par-dessus le jeu » sur un périphérique de sortie ; on
/// n'injecte rien dans le moteur son de MSFS (impossible proprement).
/// </summary>
public sealed class RadioAudioPipeline : IDisposable
{
    private readonly Random _rng = new();
    private readonly object _gate = new();
    private readonly RadioSampleRepository? _samples;
    private WaveOutEvent? _output;

    /// <param name="samples">
    /// Échantillons radio réels (optionnel). Fournis, ils remplacent les bruitages
    /// synthétisés ; absents, la chaîne se comporte exactement comme avant.
    /// </param>
    public RadioAudioPipeline(RadioSampleRepository? samples = null) => _samples = samples;

    /// <summary>Radio-ise puis joue la voix ; se termine quand la lecture est finie.</summary>
    public async Task PlayAsync(TtsAudio voice, int deviceNumber, RadioProfile profile, CancellationToken ct = default)
    {
        if (voice.IsEmpty) return;

        AttachSamples(profile, voice.SampleRate);

        float[] processed = RadioDsp.Apply(voice.Samples, voice.SampleRate, profile, _rng);
        byte[] pcm = ToPcm16(processed);
        var format = new WaveFormat(voice.SampleRate, 16, 1);

        WaveOutEvent output;
        lock (_gate)
        {
            StopInternal();                       // coupe une éventuelle transmission en cours
            output = new WaveOutEvent { DeviceNumber = deviceNumber };
            _output = output;
        }

        var stream = new RawSourceWaveStream(new MemoryStream(pcm), format);
        var tcs = new TaskCompletionSource();
        output.PlaybackStopped += (_, _) =>
        {
            stream.Dispose();
            tcs.TrySetResult();
        };

        output.Init(stream);
        using (ct.Register(() => { try { output.Stop(); } catch { } }))
        {
            output.Play();
            await tcs.Task.ConfigureAwait(false);
        }

        lock (_gate) { if (ReferenceEquals(_output, output)) _output = null; }
        output.Dispose();
    }

    /// <summary>
    /// Tire une VARIANTE de chaque échantillon pour CETTE transmission. Le tirage se fait
    /// ici, à la lecture, et non au chargement : deux transmissions consécutives ne doivent
    /// pas présenter le même déclic ni la même respiration — c'est la répétition à
    /// l'identique qui trahit un bruitage, autant qu'un mauvais son.
    /// </summary>
    private void AttachSamples(RadioProfile profile, int rate)
    {
        if (_samples is null) return;

        profile.KeyUpSample = profile.Squelch ? _samples.Pick(RadioSampleKind.KeyUp, rate, _rng) : null;
        profile.TailSample = profile.Squelch ? _samples.Pick(RadioSampleKind.Tail, rate, _rng) : null;
        profile.BreathSample = _samples.Pick(RadioSampleKind.Breath, rate, _rng);
        profile.BedSample = _samples.Pick(RadioSampleKind.Bed, rate, _rng);
        // Le FOND CONTINU (background.wav) n'est plus mélangé : le souffle permanent a été
        // retiré de l'effet radio. Les échantillons de clic d'alternat, de respiration et de
        // fond de station, eux, restent en place — ce sont des sons ponctuels, pas un bruit.
    }

    public void Stop()
    {
        lock (_gate) { StopInternal(); }
    }

    private void StopInternal()
    {
        try { _output?.Stop(); _output?.Dispose(); }
        catch { }
        _output = null;
    }

    public void Dispose() => Stop();

    private static byte[] ToPcm16(float[] x)
    {
        var bytes = new byte[x.Length * 2];
        for (int i = 0; i < x.Length; i++)
        {
            float s = Math.Clamp(x[i], -1f, 1f);
            short v = (short)(s * 32767);
            bytes[i * 2] = (byte)(v & 0xFF);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        return bytes;
    }
}
