using System.IO;
using NAudio.Wave;

namespace FreqWatch.Audio;

/// <summary>Lecture d'un flux WAV (RIFF/PCM) vers des échantillons mono float.</summary>
internal static class WavUtil
{
    public static TtsAudio ReadMono(Stream wav)
    {
        using var reader = new WaveFileReader(wav);
        var sp = reader.ToSampleProvider();
        int channels = sp.WaveFormat.Channels;
        int rate = sp.WaveFormat.SampleRate;

        var all = new List<float>();
        var tmp = new float[8192];
        int read;
        while ((read = sp.Read(tmp, 0, tmp.Length)) > 0)
            for (int i = 0; i < read; i++) all.Add(tmp[i]);

        float[] samples = all.ToArray();
        if (channels > 1) samples = Downmix(samples, channels);
        return new TtsAudio(samples, rate, 1);
    }

    private static float[] Downmix(float[] interleaved, int channels)
    {
        int frames = interleaved.Length / channels;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[f * channels + c];
            mono[f] = sum / channels;
        }
        return mono;
    }
}
