namespace FreqWatch.Audio;

/// <summary>Réglages de l'effet radio appliqué à la voix.</summary>
public sealed class RadioProfile
{
    public bool BandPass { get; set; } = true;   // passe-bande ~300–3000 Hz
    public double HighPassHz { get; set; } = 300;
    public double LowPassHz { get; set; } = 3000;

    public bool Hiss { get; set; } = true;        // souffle de fond
    public double HissLevel { get; set; } = 0.015;

    public bool Squelch { get; set; } = true;     // clic d'ouverture/fermeture
    public bool Saturation { get; set; } = true;  // légère compression/saturation
    public double SaturationDrive { get; set; } = 1.8;

    public double Volume { get; set; } = 0.9;
}
