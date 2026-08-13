using NAudio.Dsp;

namespace WilcoATC.Audio;

/// <summary>
/// Chaîne DSP « radio VHF » appliquée au PCM de la TTS :
///   1. normalisation — la voix est VOLONTAIREMENT en retrait pour que l'effet passe devant,
///   2. passe-bande ~300–3000 Hz (BiQuad HP + LP en cascade),
///   3. légère saturation (compressé),
///   4. souffle de fond — fichier background.wav s'il existe (bien présent), sinon souffle blanc,
///   5. clics de squelch à l'ouverture et à la fermeture.
/// Tout est fait sur des float[] mono ; aucune allocation native.
/// </summary>
public static class RadioDsp
{
    /// <summary>
    /// Niveau de la porteuse dans le clic d'alternat et la queue de squelch SYNTHÉTIQUES.
    ///
    /// Il valait auparavant le niveau de souffle du profil ; celui-ci ayant disparu, ces deux
    /// transitoires seraient devenus muets. Ils sont pourtant ce qui reste de la couleur radio
    /// une fois le bruit de fond retiré — d'où cette valeur propre, calée sur l'ancien réglage
    /// par défaut.
    /// </summary>
    private const double SquelchCarrier = 0.017;

    /// <summary>Crête de normalisation de la voix. Basse -> voix bien EN RETRAIT sous les effets.</summary>
    private const float VoicePeak = 0.42f;

    public static float[] Apply(float[] voice, int rate, RadioProfile p, Random rng)
    {
        var v = (float[])voice.Clone();

        Normalize(v, VoicePeak);
        if (p.BandPass) BandPass(v, rate, p.HighPassHz, p.LowPassHz);
        if (p.Saturation) Saturate(v, p.SaturationDrive);

        // PLUS DE SOUFFLE DE FOND. Il courait sous la voix et remplissait les segments de
        // porteuse : un bruit constant, présent à chaque transmission, qui fatigue à l'usage
        // bien plus qu'il n'ajoute de réalisme — et qui masquait une partie de l'intelligibilité
        // du contrôleur, laquelle est tout l'intérêt du logiciel.
        //
        // Ce qui RESTE : le clic d'alternat et la queue de squelch. Ce sont des transitoires,
        // pas du bruit de fond ; ce sont eux qui font « radio », et on ne les perd pas.

        // ASSEMBLAGE — on rejoue le geste d'une PERSONNE, pas un effet sonore :
        //   [alternat enfoncé] [porteuse ouverte] [voix] [porteuse ouverte] [queue de squelch]

        // Un ÉCHANTILLON RÉEL l'emporte sur la synthèse ; à défaut on retombe sur le synthétisé.
        float[] open = p.KeyUpSample
                       ?? (p.Squelch ? KeyUp(rate, rng, SquelchCarrier) : Array.Empty<float>());
        float[] close = p.TailSample
                        ?? (p.Squelch ? SquelchTail(rate, rng, SquelchCarrier) : Array.Empty<float>());

        // La respiration n'a AUCUN repli synthétique : sans fichier, on n'en joue pas.
        float[] breath = p.BreathSample ?? Array.Empty<float>();

        // Segments SILENCIEUX avant et après la voix. On les garde : ce sont eux qui donnent
        // son rythme à la transmission — l'alternat s'enfonce, un instant passe, puis on parle.
        // Les supprimer ferait démarrer la voix collée au clic, ce qui s'entend tout de suite.
        float[] pre = new float[Duration(rate, 0.13, 0.24, rng)];
        float[] post = new float[Duration(rate, 0.08, 0.16, rng)];

        float[] outp = Concat(open, pre, breath, v, post, close);

        // FOND DE LA STATION ÉMETTRICE (bed) — court de l'ouverture à la fin de la parole.
        if (p.BedSample is { Length: > 0 } bed && p.BedVolume > 0)
        {
            int bedFrom = open.Length;
            int bedTo = outp.Length - close.Length;
            MixLoop(outp, bed, bedFrom, bedTo, (float)p.BedVolume, rng);
        }

        Gain(outp, (float)p.Volume);
        Limit(outp);
        return outp;
    }

    /// <summary>
    /// Mélange un échantillon EN BOUCLE sur un intervalle, avec un fondu d'entrée et de sortie de
    /// 15 ms (sinon la boucle claque au raccord). Départ dans la boucle tiré au hasard.
    /// </summary>
    private static void MixLoop(float[] target, float[] loop, int from, int to, float level, Random rng)
    {
        if (loop.Length == 0 || to <= from) return;

        int fade = Math.Min((int)(0.015 * 22050), (to - from) / 2);
        int offset = rng.Next(loop.Length);

        for (int i = from; i < to; i++)
        {
            float env = 1f;
            if (i - from < fade) env = (i - from) / (float)fade;
            else if (to - i < fade) env = (to - i) / (float)fade;

            target[i] += loop[(offset + i - from) % loop.Length] * level * env;
        }
    }

    // --- filtres ---------------------------------------------------------

    private static void BandPass(float[] x, int rate, double hp, double lp)
    {
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

    // --- squelch ----------------------------------------------------------
    //
    // Les générateurs de souffle (bruit blanc, mixage du fond enregistré, lits de porteuse)
    // ont été supprimés avec le souffle lui-même. Seuls subsistent le clic d'alternat et la
    // queue de squelch, ci-dessous : des transitoires, pas du bruit continu.

    /// <summary>Durée aléatoire en échantillons, entre deux bornes en secondes.</summary>
    private static int Duration(int rate, double minSec, double maxSec, Random rng)
        => (int)(rate * (minSec + rng.NextDouble() * (maxSec - minSec)));

    /// <summary>DÉBUT D'ÉMISSION — clic mécanique de l'alternat + ouverture de porteuse.</summary>
    private static float[] KeyUp(int rate, Random rng, double hiss)
    {
        int n = (int)(rate * 0.09);                 // ~90 ms
        var a = new float[n];
        var lp = BiQuadFilter.LowPassFilter(rate, 900f, 0.9f);
        var hp = BiQuadFilter.HighPassFilter(rate, 1200f, 0.7f);

        double clunkTau = rate * 0.012;
        double burstTau = rate * 0.009;

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;

            double clunkEnv = Math.Exp(-i / clunkTau);
            double clunk = lp.Transform((float)(
                (Math.Sin(2 * Math.PI * 108 * t) * 0.7 + Math.Sin(2 * Math.PI * 173 * t) * 0.3)
                * clunkEnv));

            double burst = hp.Transform((float)(rng.NextDouble() * 2 - 1)) * Math.Exp(-i / burstTau);

            double ramp = Math.Min(1.0, t / 0.015);
            double carrier = (rng.NextDouble() * 2 - 1) * hiss * ramp;

            a[i] = (float)(clunk * 0.34 + burst * 0.20 + carrier);
        }
        return a;
    }

    /// <summary>FIN D'ÉMISSION — « queue de squelch » : bouffée de bruit forte + relâchement.</summary>
    private static float[] SquelchTail(int rate, Random rng, double hiss)
    {
        int n = (int)(rate * 0.13);                 // ~130 ms
        var a = new float[n];
        var hp = BiQuadFilter.HighPassFilter(rate, 800f, 0.6f);
        var lp = BiQuadFilter.LowPassFilter(rate, 5200f, 0.6f);

        double tau = rate * 0.030;                  // décroissance ~30 ms
        double relTau = rate * 0.010;

        // Crête au-dessus du souffle, avec un plancher : la queue reste audible même souffle coupé.
        double peak = Math.Max(0.30, hiss * 12);

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / rate;

            double noise = lp.Transform(hp.Transform((float)(rng.NextDouble() * 2 - 1)));
            double burst = noise * Math.Exp(-i / tau) * peak;

            double release = Math.Sin(2 * Math.PI * 92 * t) * Math.Exp(-i / relTau) * 0.15;

            a[i] = (float)(burst + release);
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
