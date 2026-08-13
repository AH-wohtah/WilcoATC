namespace WilcoATC.Audio;

/// <summary>Réglages de l'effet radio appliqué à la voix.</summary>
public sealed class RadioProfile
{
    public bool BandPass { get; set; } = true;   // passe-bande
    public double HighPassHz { get; set; } = 275;
    public double LowPassHz { get; set; } = 4300;


    public bool Squelch { get; set; } = true;     // clic d'ouverture/fermeture
    public bool Saturation { get; set; } = true;  // légère compression/saturation
    public double SaturationDrive { get; set; } = 1.9;

    public double Volume { get; set; } = 0.9;

    // --- Échantillons RÉELS (voir RadioSampleRepository) ---
    // Renseignés juste avant la lecture, une variante tirée au hasard à chaque
    // transmission. Null = on retombe sur le synthétisé (alternat, queue) ou on ne joue
    // rien du tout (respiration, fond de cockpit).

    /// <summary>Déclic d'alternat. Null -> synthèse.</summary>
    public float[]? KeyUpSample { get; set; }

    /// <summary>Queue de squelch. Null -> synthèse.</summary>
    public float[]? TailSample { get; set; }

    /// <summary>Respiration du pilote avant de parler. Null -> rien (aucune synthèse).</summary>
    public float[]? BreathSample { get; set; }

    /// <summary>Fond sonore de la station émettrice. Null -> rien (aucune synthèse).</summary>
    public float[]? BedSample { get; set; }

    /// <summary>Niveau du fond de cockpit sous la voix (0 = muet).</summary>
    public double BedVolume { get; set; } = 0.35;

    /// <summary>
    /// Construit un profil à partir d'une INTENSITÉ unique (0 = presque propre, 1 = radio
    /// très marquée). Les quatre paramètres bougent ensemble : les régler séparément n'a
    /// pas de sens à l'oreille, et le réglage précédent (300–3000 Hz en dur) correspondait
    /// déjà à ~0,9 — d'où une voix inutilement dégradée par défaut.
    ///
    /// À 0,5 la bande reste large (275–4300 Hz) : on entend la radio sans perdre le timbre.
    /// </summary>
    public static RadioProfile FromIntensity(double intensity) => new()
    {
        HighPassHz = Lerp(150, 400, intensity),
        LowPassHz = Lerp(6000, 2600, intensity),
        SaturationDrive = Lerp(1.2, 2.6, intensity),
    };

    private static double Lerp(double a, double b, double t)
        => a + (b - a) * Math.Clamp(t, 0, 1);
}
