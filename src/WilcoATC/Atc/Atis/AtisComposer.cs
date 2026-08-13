using System.Globalization;
using System.Text;
using WilcoATC.Formatting;
using WilcoATC.Sim;

namespace WilcoATC.Atc.Atis;

/// <summary>
/// Met un <see cref="AtisReport"/> en mots. Phraséologie ANGLAISE (standard OACI), comme
/// tout ce que dit la radio dans l'application.
///
/// Les nombres sont ÉPELÉS chiffre par chiffre (« two four zero degrees ») : c'est la
/// prononciation radio, et c'est aussi la seule façon d'empêcher le moteur de synthèse de
/// lire « 240 » en « two hundred forty ». On s'en tient aux noms de chiffres ordinaires,
/// sans les formes strictes « tree / fife / niner » : mélangées à des mots normaux elles
/// s'entendent comme des erreurs de lecture plutôt que comme de la radio.
///
/// Un élément qu'on ne connaît pas est OMIS, jamais deviné : un bulletin sans température
/// reste un bulletin utilisable, un bulletin qui annonce une fausse température ne l'est pas.
/// </summary>
public static class AtisComposer
{
    /// <summary>Visibilité à partir de laquelle un bulletin dit « ou plus » (mètres).</summary>
    private const int VisibilityUnlimitedMeters = 9_999;

    public static string Compose(AtisReport r)
    {
        string letter = Nato(r.Letter);
        var c = r.Conditions;
        var parts = new List<string>();

        string name = string.IsNullOrWhiteSpace(r.AirportName) ? "" : r.AirportName.Trim() + " ";
        parts.Add($"{name}information {letter}.");
        parts.Add($"Time {Digits(r.ZuluTime.Hours * 100 + r.ZuluTime.Minutes, 4)} zulu.");

        if (!string.IsNullOrWhiteSpace(r.Runway))
            parts.Add($"{Capitalize(RunwayFormatter.Speak(r.Runway))} in use.");

        parts.Add(c.IsCalm
            ? "Wind calm."
            : $"Wind {Digits(c.WindDirectionDeg, 3)} degrees, {Digits(c.WindSpeedKnots)} knots.");

        parts.Add(Visibility(c.VisibilityMeters));

        string? precip = Precipitation(c.Precipitation);
        if (precip is not null) parts.Add(precip);

        parts.Add(c.TemperatureC < 0
            ? $"Temperature minus {Digits(-c.TemperatureC)}."
            : $"Temperature {Digits(c.TemperatureC)}.");

        if (c.QnhHectopascals > 0) parts.Add(Pressure(c.QnhHectopascals, r.Icao));

        parts.Add($"Advise on initial contact you have information {letter}.");

        return string.Join(" ", parts);
    }

    private static string Visibility(int meters)
    {
        if (meters >= VisibilityUnlimitedMeters) return "Visibility one zero kilometers or more.";
        if (meters >= 1_000) return $"Visibility {Digits((int)Math.Round(meters / 1000.0))} kilometers.";
        return $"Visibility {Digits(meters)} meters.";
    }

    private static string? Precipitation(PrecipKind p) => p switch
    {
        PrecipKind.Rain => "Rain.",
        PrecipKind.Snow => "Snow.",
        _ => null,   // aucune précipitation, ou masque non reconnu -> on n'en parle pas
    };

    /// <summary>
    /// Pression : « QNH 1013 » partout, sauf aux États-Unis où l'usage — et donc l'ATIS —
    /// est le calage en pouces de mercure (« altimeter two nine nine two »). Les préfixes K
    /// et P couvrent le continent, l'Alaska et le Pacifique américain.
    /// </summary>
    private static string Pressure(int hectopascals, string? icao)
    {
        bool inchesOfMercury = icao is { Length: 4 } && (icao[0] == 'K' || icao[0] == 'P');
        if (!inchesOfMercury) return $"Q N H {Digits(hectopascals)}.";

        // 1013 hPa -> 29.91 inHg, annoncé sans virgule : « two nine nine one ».
        int inHgHundredths = (int)Math.Round(hectopascals / 33.8639 * 100);
        return $"Altimeter {Digits(inHgHundredths, 4)}.";
    }

    /// <summary>Nombre épelé chiffre par chiffre, complété à gauche à <paramref name="minLength"/>.</summary>
    private static string Digits(int value, int minLength = 1)
    {
        string s = Math.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(minLength, '0');
        var sb = new StringBuilder(s.Length * 5);
        foreach (char d in s)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Word(d));
        }
        return sb.ToString();
    }

    private static string Word(char digit) => digit switch
    {
        '0' => "zero", '1' => "one", '2' => "two", '3' => "three", '4' => "four",
        '5' => "five", '6' => "six", '7' => "seven", '8' => "eight", '9' => "nine",
        _ => digit.ToString(),
    };

    private static string Capitalize(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Nato(char c) => char.ToUpperInvariant(c) switch
    {
        'A' => "Alpha", 'B' => "Bravo", 'C' => "Charlie", 'D' => "Delta", 'E' => "Echo",
        'F' => "Foxtrot", 'G' => "Golf", 'H' => "Hotel", 'I' => "India", 'J' => "Juliet",
        'K' => "Kilo", 'L' => "Lima", 'M' => "Mike", 'N' => "November", 'O' => "Oscar",
        'P' => "Papa", 'Q' => "Quebec", 'R' => "Romeo", 'S' => "Sierra", 'T' => "Tango",
        'U' => "Uniform", 'V' => "Victor", 'W' => "Whiskey", 'X' => "X-ray", 'Y' => "Yankee",
        'Z' => "Zulu",
        _ => c.ToString(),
    };
}
