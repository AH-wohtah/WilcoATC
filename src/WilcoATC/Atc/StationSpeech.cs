using System.Text.RegularExpressions;

namespace FreqWatch.Atc;

/// <summary>
/// Rend un nom de station prononçable par la TTS : « Paris CDG · TWR » -> « Paris CDG Tower ».
/// Les abréviations OurAirports (TWR, GND, APP…) sont développées en mots.
/// </summary>
public static class StationSpeech
{
    private static readonly (string Abbr, string Word)[] Map =
    {
        ("TWR", "Tower"), ("GND", "Ground"), ("APP", "Approach"), ("DEP", "Departure"),
        ("ATIS", "Information"), ("CTR", "Control"), ("CLR", "Clearance"),
        ("DEL", "Delivery"), ("UNIC", "Unicom"), ("FSS", "Radio"), ("APRON", "Apron"),
        ("RDO", "Radio"), ("CTAF", "Traffic"),
    };

    public static string Prettify(string? station, string? icao)
    {
        if (string.IsNullOrWhiteSpace(station))
            return string.IsNullOrWhiteSpace(icao) ? "Approach" : icao!;

        string s = station.Replace(" · ", " ").Replace("·", " ");
        foreach (var (abbr, word) in Map)
            s = Regex.Replace(s, $@"\b{Regex.Escape(abbr)}\b", word, RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}
