namespace FreqWatch.Audio;

/// <summary>
/// Audio synthétisé par un moteur TTS : échantillons PCM en virgule flottante
/// (mono après normalisation par le moteur), avec la fréquence d'échantillonnage.
/// </summary>
public sealed record TtsAudio(float[] Samples, int SampleRate, int Channels)
{
    public static readonly TtsAudio Empty = new(Array.Empty<float>(), 22050, 1);
    public bool IsEmpty => Samples.Length == 0;
}
