using System.IO;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FreqWatch.Audio;

/// <summary>
/// Lecteur de fichiers audio pour l'ambiance CABINE (packs de sons). Indépendant du canal
/// voix : les annonces cabine se superposent volontairement à la radio (comme dans l'avion).
/// Une seule annonce cabine à la fois (une nouvelle remplace la précédente).
///
/// Formats : tout ce que NAudio lit nativement (.wav, .mp3).
/// </summary>
public sealed class CabinAudioPlayer : IDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private WaveStream? _reader;

    /// <summary>Extensions lisibles (l'OGG passe par NAudio.Vorbis).</summary>
    public static readonly string[] SupportedExtensions = { ".wav", ".mp3", ".ogg" };

    /// <summary>Joue un fichier (non bloquant). Volume 0..1. Échec silencieux si illisible.</summary>
    public void Play(string path, int deviceNumber, double volume)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        // Tant que la lecture n'a pas démarré, C'EST NOUS qui possédons ces objets : en cas
        // d'échec (fichier corrompu, périphérique occupé…) il faut les libérer, sinon le
        // fichier reste VERROUILLÉ et l'utilisateur ne peut plus le remplacer/supprimer.
        WaveStream? reader = null;
        WaveOutEvent? output = null;
        try
        {
            reader = OpenReader(path);
            // Volume appliqué via un provider : VorbisWaveReader n'a pas de propriété Volume.
            ISampleProvider samples = reader is ISampleProvider sp ? sp : reader.ToSampleProvider();
            var withVolume = new VolumeSampleProvider(samples) { Volume = (float)Math.Clamp(volume, 0, 1) };

            output = new WaveOutEvent { DeviceNumber = deviceNumber };
            var startedReader = reader;
            var startedOutput = output;

            startedOutput.PlaybackStopped += (_, _) =>
            {
                try { startedReader.Dispose(); } catch { }
                try { startedOutput.Dispose(); } catch { }
                lock (_gate)
                {
                    if (ReferenceEquals(_output, startedOutput)) { _output = null; _reader = null; }
                }
            };

            output.Init(withVolume);   // peut lever si le flux est illisible

            lock (_gate)
            {
                StopInternal();
                _reader = startedReader;
                _output = startedOutput;
            }

            output.Play();
            reader = null; output = null;  // la lecture a démarré : PlaybackStopped libérera
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[WilcoATC/Cabin] lecture : " + ex);
        }
        finally
        {
            try { output?.Dispose(); } catch { }
            try { reader?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Ouvre le bon décodeur selon l'extension (.ogg -> Vorbis, sinon NAudio).
    ///
    /// Pour l'OGG on ouvre le FICHIER NOUS-MÊMES avant de le confier au décodeur : si le
    /// fichier est corrompu, le constructeur Vorbis lève APRÈS avoir ouvert le flux, et
    /// sans cette précaution on n'aurait aucune référence pour le refermer — le fichier
    /// resterait verrouillé (impossible de le remplacer) jusqu'au passage du GC.
    /// </summary>
    private static WaveStream OpenReader(string path)
    {
        if (!Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            return new AudioFileReader(path);

        FileStream? stream = null;
        try
        {
            stream = File.OpenRead(path);
            return new VorbisWaveReader(stream, closeOnDispose: true);
        }
        catch
        {
            try { stream?.Dispose(); } catch { }
            throw;
        }
    }

    public void Stop()
    {
        lock (_gate) { StopInternal(); }
    }

    private void StopInternal()
    {
        try { _output?.Stop(); } catch { }
        try { _output?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        _output = null;
        _reader = null;
    }

    public void Dispose() => Stop();
}
