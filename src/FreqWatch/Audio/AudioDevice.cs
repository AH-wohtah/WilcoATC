namespace FreqWatch.Audio;

/// <summary>Périphérique de sortie audio (Number = -1 pour le périphérique par défaut).</summary>
public sealed record AudioDevice(int Number, string Name)
{
    public override string ToString() => Name;
}
