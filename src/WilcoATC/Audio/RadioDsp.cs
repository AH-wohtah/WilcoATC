using NAudio.Dsp;

namespace FreqWatch.Audio;

/// <summary>
/// Chaîne DSP « radio VHF » appliquée au PCM de la TTS :
///   1. normalisation,
///   2. passe-bande ~300–3000 Hz (BiQuad HP + LP en cascade),
///   3. légère saturation (compressé),
///   4. souffle de fond,
///   5. clics de squelch à l'ouverture et à la fermeture.
/// Tout est fait sur des float[] mono ; aucune allocation native.
/// </summary>
public static class RadioDsp
{
    public static float[] Apply(float[] voice, int rate, RadioProfile p, Random rng)
    {
        var v = (float[])voice.Clone();

        Normalize(v, 0.9f);
        if (p.BandPass) BandPass(v, rate, p.HighPassHz, p.LowPassHz);
        if (p.Saturation) Saturate(v, p.SaturationDrive);
        if (p.Hiss) AddHissInPlace(v, p.HissLevel, rng);

        // Assemblage : [clic ouverture] [souffle court] [voix] [souffle court] [clic fermeture]
        double hiss = p.Hiss ? p.HissLevel : 0;
        float[] open = p.Squelch ? SquelchClick(rate, rng) : Array.Empty<float>();
        float[] pre = MakeHiss((int)(rate * 0.05), hiss, rng);
        float[] post = MakeHiss((int)(rate * 0.10), hiss, rng);
        float[] close = p.Squelch ? SquelchClick(rate, rng) : Array.Empty<float>();

        float[] outp = Concat(open, pre, v, post, close);

        Gain(outp, (float)p.Volume);
        Limit(outp);
        return outp;
    }

    // --- filtres ---------------------------------------------------------

    private static void BandPass(float[] x, int rate, double hp, double lp)
    {
        // Deux étages HP + deux étages LP -> pentes plus marquées, son plus « radio ».
        var hp1 = BiQuadFilter.HighPassFilter(rate, (float)hp, 0.7f);
        var hp2 = BiQuadFilter.HighPassFilter(rate, (float)hp, 0.7f);
        var lp1 = BiQuadFilter.LowPassFilter(rate, (float)lp, 0.7f);
        var lp2 = BiQuadFilter.LowPassFilter(rate, (float)lp, 0.7f);
        for (int i = 0; i < x.Length; i++)
        {
            float s = x[i];
            s = hp1.Transform(s);
            s = hp2.Transform(s);
            s = lp1.Transform(s);
            s = lp2.Transform(s);
            x[i] = s;
        }
    }

    private static void Saturate(float[] x, double drive)
    {
        double d = Math.Max(1.0, drive);
        double norm = Math.Tanh(d);
        for (int i = 0; i < x.Length; i++)
            x[i] = (float)(Math.Tanh(x[i] * d) / norm);
    }

    // --- souffle & squelch ----------------------------------------------

    private static void AddHissInPlace(float[] x, double level, Random rng)
    {
        for (int i = 0; i < x.Length; i++)
            x[i] += (float)((rng.NextDouble() * 2 - 1) * level);
    }

    private static float[] MakeHiss(int n, double level, Random rng)
    {
        var a = new float[Math.Max(0, n)];
        for (int i = 0; i < a.Length; i++)
            a[i] = (float)((rng.NextDouble() * 2 - 1) * level);
        return a;
    }

    // Clic de squelch : courte bouffée de bruit filtrée, à décroissance rapide.
    private static float[] SquelchClick(int rate, Random rng)
    {
        int n = (int)(rate * 0.05); // ~50 ms
        var a = new float[n];
        var hp = BiQuadFilter.HighPassFilter(rate, 700f, 0.7f);
        double tau = rate * 0.008; // décroissance ~8 ms
        for (int i = 0; i < n; i++)
        {
            double env = Math.Exp(-i / tau);
            double noise = rng.NextDouble() * 2 - 1;
            a[i] = hp.Transform((float)(noise * env)) * 0.6f;
        }
        return a;
    }

    // --- utilitaires -----------------------------------------------------

    private static void Normalize(float[] x, float peak)
    {
        float max = 1e-6f;
        foreach (var s in x) max = Math.Max(max, Math.Abs(s));
        float g = peak / max;
        for (int i = 0; i < x.Length; i++) x[i] *= g;
    }

    private static void Gain(float[] x, float g)
    {
        for (int i = 0; i < x.Length; i++) x[i] *= g;
    }

    private static void Limit(float[] x)
    {
        for (int i = 0; i < x.Length; i++) x[i] = Math.Clamp(x[i], -1f, 1f);
    }

    private static float[] Concat(params float[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var outp = new float[total];
        int o = 0;
        foreach (var p in parts) { Array.Copy(p, 0, outp, o, p.Length); o += p.Length; }
        return outp;
    }
}
