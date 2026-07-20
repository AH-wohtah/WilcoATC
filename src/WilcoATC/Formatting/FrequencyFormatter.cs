using System.Globalization;

namespace FreqWatch.Formatting;

/// <summary>
/// Formatage et comparaison des fréquences COM.
///
/// CHOIX DE L'UNITÉ SimConnect : « Hz » (FLOAT64).
/// -----------------------------------------------
/// Les fréquences COM peuvent être demandées à SimConnect dans plusieurs unités :
///   • "Frequency BCD16" : renvoie un entier codé BCD. Décodage fastidieux et,
///     surtout, incapable de représenter proprement l'espacement 8.33 kHz.
///   • "MHz" : renvoie un flottant en MHz (ex. 118.7) — pratique mais on préfère…
///   • "Hz"  : renvoie un nombre ENTIER de hertz exact (ex. 118700000). C'est ce
///     qu'on retient : aucun décodage BCD, précision suffisante pour le 8.33 kHz
///     (canaux type 118.305), et comparaison de changement triviale.
///
/// AFFICHAGE : on convertit en MHz (÷ 1 000 000) puis on formate à 3 décimales.
/// Cela couvre proprement l'espacement 25 kHz (118.700, 121.500…) ET 8.33 kHz
/// (118.305, 118.310…).
/// </summary>
public static class FrequencyFormatter
{
    // En deçà de ce seuil (1 MHz), on considère qu'il n'y a pas de fréquence valide.
    private const double MinValidHz = 1_000_000.0;

    /// <summary>Formate une fréquence exprimée en Hz vers "118.700" (MHz, 3 décimales).</summary>
    public static string FormatMHz(double hz)
    {
        if (hz < MinValidHz)
            return "---.---"; // pas de radio / valeur non initialisée

        double mhz = hz / 1_000_000.0;
        // Arrondi au kHz près pour absorber toute imprécision flottante, puis
        // 3 décimales fixes. "000.000" garantit 3 chiffres entiers (les COM sont
        // toujours entre 118 et 137 MHz).
        mhz = Math.Round(mhz, 3, MidpointRounding.AwayFromZero);
        return mhz.ToString("000.000", CultureInfo.InvariantCulture);
    }

    /// <summary>Vrai si deux fréquences (en Hz) désignent le même canal (tolérance ±500 Hz).</summary>
    public static bool SameChannel(double aHz, double bHz)
        => Math.Abs(aHz - bHz) < 500.0;

    /// <summary>
    /// Prononciation d'une fréquence comme un NOMBRE (chiffres), pas en mots épelés :
    /// on laisse le moteur TTS lire le nombre naturellement. Le séparateur décimal suit
    /// la langue (virgule en FR → lu « virgule », point en EN → lu « point »). Les zéros
    /// finaux non significatifs sont retirés ; la précision 8.33 kHz est conservée.
    /// Ex. 118.700 → FR « 118,7 » (« cent dix-huit virgule sept »), EN « 118.7 ».
    /// </summary>
    public static string Speak(double hz, bool french)
    {
        double mhz = hz / 1_000_000.0;
        mhz = Math.Round(mhz, 3, MidpointRounding.AwayFromZero);
        string s = mhz.ToString("0.###", CultureInfo.InvariantCulture); // 118.700 -> "118.7"
        return french ? s.Replace('.', ',') : s;
    }
}
