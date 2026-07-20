using System.Text.RegularExpressions;

namespace FreqWatch.Formatting;

/// <summary>
/// Prononciation d'un nom de SID/STAR : « {nom}{chiffre}{lettre} » →
/// « {nom} {chiffre} {lettre phonétique OACI} ». Ex. SOSAL2Y → « SOSAL 2 Yankee »,
/// DEGES3W → « DEGES 3 Whiskey ». Format inattendu → nom brut.
/// </summary>
public static class SidFormatter
{
    public static string? Speak(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid)) return null;

        string s = sid.Trim().ToUpperInvariant();
        var m = Regex.Match(s, @"^([A-Z]+)(\d+)([A-Z])?$");
        if (!m.Success) return sid.Trim(); // ex. nom sans révision -> tel quel

        string name = m.Groups[1].Value;
        string num = m.Groups[2].Value;
        string suffix = m.Groups[3].Value;

        string result = $"{name} {num}";
        if (suffix.Length == 1) result += " " + Nato(suffix[0]);
        return result;
    }

    private static string Nato(char c) => c switch
    {
        'A' => "Alpha", 'B' => "Bravo", 'C' => "Charlie", 'D' => "Delta", 'E' => "Echo",
        'F' => "Foxtrot", 'G' => "Golf", 'H' => "Hotel", 'I' => "India", 'J' => "Juliet",
        'K' => "Kilo", 'L' => "Lima", 'M' => "Mike", 'N' => "November", 'O' => "Oscar",
        'P' => "Papa", 'Q' => "Quebec", 'R' => "Romeo", 'S' => "Sierra", 'T' => "Tango",
        'U' => "Uniform", 'V' => "Victor", 'W' => "Whiskey", 'X' => "X-ray", 'Y' => "Yankee", 'Z' => "Zulu",
        _ => c.ToString(),
    };
}
