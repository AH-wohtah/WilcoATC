using System.Text;

namespace FreqWatch.Formatting;

/// <summary>
/// Prononciation d'une piste : « 34R » -> « runway 3 4 right ».
///
/// La phrase RENVOYÉE CONTIENT le mot « runway ». C'est volontaire : quand la piste est
/// inconnue (aucun plan de vol chargé), on renvoie « the active runway », qui s'insère
/// naturellement là où un numéro serait allé — au lieu d'inventer une piste, ce que faisait
/// l'ancienne valeur codée en dur.
/// </summary>
public static class RunwayFormatter
{
    /// <summary>Formule employée quand la piste n'est pas connue.</summary>
    public const string Unknown = "the active runway";

    public static string Speak(string? runway)
    {
        if (string.IsNullOrWhiteSpace(runway)) return Unknown;

        var digits = new StringBuilder();
        string side = "";

        foreach (char c in runway!.Trim().ToUpperInvariant())
        {
            if (char.IsDigit(c))
            {
                if (digits.Length > 0) digits.Append(' ');   // épelé chiffre par chiffre
                digits.Append(c);
            }
            else
            {
                side = c switch
                {
                    'L' => " left",
                    'R' => " right",
                    'C' => " center",
                    _ => side,
                };
            }
        }

        return digits.Length == 0 ? Unknown : $"runway {digits}{side}";
    }
}
