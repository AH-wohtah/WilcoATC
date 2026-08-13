using System.Text;
using WilcoATC.Atc;
using WilcoATC.Atc.Localization;

namespace WilcoATC.Formatting;

/// <summary>
/// Prononciation d'une piste : « 34R » -> « runway 3 4 right », « piste 3 4 droite »…
///
/// La phrase RENVOYÉE CONTIENT le mot « piste ». C'est volontaire : quand la piste est
/// inconnue (aucun plan de vol chargé), on renvoie « la piste en service », qui s'insère
/// naturellement là où un numéro serait allé — au lieu d'inventer une piste, ce que faisait
/// l'ancienne valeur codée en dur.
///
/// Les CHIFFRES restent des chiffres, épelés un à un : c'est la synthèse vocale qui les lit
/// dans sa langue. Seuls le mot « piste » et le côté sont traduits.
/// </summary>
public static class RunwayFormatter
{
    /// <summary>Formule employée quand la piste n'est pas connue.</summary>
    public static string Unknown(AtcLanguage lang = AtcLanguage.English) => AtcPhrases.ActiveRunway(lang);

    public static string Speak(string? runway, AtcLanguage lang = AtcLanguage.English)
    {
        if (string.IsNullOrWhiteSpace(runway)) return Unknown(lang);

        var digits = new StringBuilder();
        char side = '\0';

        foreach (char c in runway!.Trim().ToUpperInvariant())
        {
            if (char.IsDigit(c))
            {
                if (digits.Length > 0) digits.Append(' ');   // épelé chiffre par chiffre
                digits.Append(c);
            }
            else if (c is 'L' or 'R' or 'C')
            {
                side = c;
            }
        }

        return digits.Length == 0 ? Unknown(lang) : AtcPhrases.Runway(lang, digits.ToString(), side);
    }
}
