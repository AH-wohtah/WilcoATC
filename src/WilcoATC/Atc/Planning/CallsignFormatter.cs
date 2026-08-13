using System.Text;
using System.Text.RegularExpressions;
using WilcoATC.Formatting;

namespace WilcoATC.Atc.Planning;

/// <summary>
/// Construit l'indicatif PARLÉ, réutilisable partout :
///  - vol de ligne (compagnie + numéro connus) -> « {télophonie} {numéro} » (ex. « Emirates 231 ») ;
///  - sinon (aviation générale) -> immatriculation épelée en alphabet phonétique OACI
///    (G-FBIG -> « Golf Foxtrot Bravo India Golf »).
/// </summary>
public sealed class CallsignFormatter
{
    private readonly AirlineTelephony _airlines;
    private readonly FlightPlanStore _plans;

    public CallsignFormatter(AirlineTelephony airlines, FlightPlanStore plans)
    {
        _airlines = airlines;
        _plans = plans;
    }

    /// <summary>Indicatif parlé, en privilégiant le plan de vol courant, sinon l'immat fournie.</summary>
    public string Speak(string? tailNumber)
    {
        var plan = _plans.Current;
        if (plan is not null)
        {
            // Vol de ligne connu -> télophonie + numéro (« Air France 1462 »).
            if (!string.IsNullOrWhiteSpace(plan.AirlineIcao) && !string.IsNullOrWhiteSpace(plan.FlightNumber))
            {
                string telephony = _airlines.Lookup(plan.AirlineIcao) ?? plan.AirlineIcao!;
                return $"{telephony} {plan.FlightNumber!.Trim()}";
            }

            // Indicatif libre saisi (immat GA, indicatif non standard) -> on le parle correctement
            // (télophonie si c'est en fait un vol de ligne, sinon épelé) au lieu de l'ignorer.
            if (!string.IsNullOrWhiteSpace(plan.AtcCallsign))
                return SpeakCallsign(plan.AtcCallsign);
        }

        return Phonetic(tailNumber);
    }

    /// <summary>
    /// Indicatif parlé à partir d'une CHAÎNE saisie (pas d'un plan) : « BEL123 » -> télophonie
    /// compagnie + numéro (« Brussels Airlines 123 »), sinon on épelle (immat GA).
    /// </summary>
    public string SpeakCallsign(string? callsign)
    {
        var (airline, number) = ParseAirlineFlight(callsign);
        if (airline is not null && number is not null)
            return $"{_airlines.Lookup(airline) ?? airline} {number}";
        return Phonetic(callsign);
    }

    /// <summary>
    /// Sépare un indicatif de ligne « BEL123 » en (compagnie OACI, numéro). Renvoie (null, null)
    /// si ça ne ressemble pas à un vol de ligne (immatriculation d'aviation générale, etc.).
    /// </summary>
    public static (string? AirlineIcao, string? FlightNumber) ParseAirlineFlight(string? callsign)
    {
        if (string.IsNullOrWhiteSpace(callsign)) return (null, null);
        var m = Regex.Match(callsign.Trim(), @"^([A-Za-z]{2,3})\s*0*(\d{1,4}[A-Za-z]?)$");
        return m.Success
            ? (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value.ToUpperInvariant())
            : (null, null);
    }

    /// <summary>Épelle une immatriculation en alphabet phonétique OACI.</summary>
    public static string Phonetic(string? registration)
    {
        if (string.IsNullOrWhiteSpace(registration)) return "Aircraft";

        var sb = new StringBuilder();
        foreach (char c in registration.ToUpperInvariant())
        {
            string? word = Word(c);
            if (word is null) continue; // ignore tirets, espaces, etc.
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(word);
        }
        return sb.Length == 0 ? "Aircraft" : sb.ToString();
    }

    private static string? Word(char c) => c switch
    {
        'A' => "Alpha", 'B' => "Bravo", 'C' => "Charlie", 'D' => "Delta", 'E' => "Echo",
        'F' => "Foxtrot", 'G' => "Golf", 'H' => "Hotel", 'I' => "India", 'J' => "Juliet",
        'K' => "Kilo", 'L' => "Lima", 'M' => "Mike", 'N' => "November", 'O' => "Oscar",
        'P' => "Papa", 'Q' => "Quebec", 'R' => "Romeo", 'S' => "Sierra", 'T' => "Tango",
        'U' => "Uniform", 'V' => "Victor", 'W' => "Whiskey", 'X' => "X-ray", 'Y' => "Yankee", 'Z' => "Zulu",
        '0' => "Zero", '1' => "One", '2' => "Two", '3' => "Three", '4' => "Four",
        '5' => "Five", '6' => "Six", '7' => "Seven", '8' => "Eight", '9' => "Nine",
        _ => null,
    };
}
