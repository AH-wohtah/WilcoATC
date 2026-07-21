namespace FreqWatch.Formatting;

/// <summary>
/// Décodage du code transpondeur.
///
/// « TRANSPONDER CODE:1 » est renvoyé en BCO16 : chaque quartet hexadécimal du
/// nombre correspond à un chiffre (octal, 0-7) du code squawk affiché.
/// Ex. la valeur brute 0x1200 correspond au squawk « 1200 ».
/// </summary>
public static class TransponderFormatter
{
    /// <summary>Convertit la valeur brute BCO16 en code numérique lisible (ex. 1200).</summary>
    public static int ToCode(double bcd)
    {
        int raw = (int)Math.Round(bcd);
        int code = 0, factor = 1;
        for (int i = 0; i < 4; i++)
        {
            int nibble = raw & 0xF;      // un chiffre du squawk
            code += nibble * factor;      // reconstruit le nombre décimal affiché
            factor *= 10;
            raw >>= 4;
        }
        return code;
    }

    /// <summary>Formate le code sur 4 chiffres (ex. 700 -> "0700").</summary>
    public static string Format(int code) => code.ToString("D4");
}
