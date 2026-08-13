using System.Text.RegularExpressions;
using WilcoATC.Atc.Localization;
using WilcoATC.Common;

namespace WilcoATC.Atc;

/// <summary>
/// Makes a station name pronounceable by the TTS: "Paris CDG · TWR" -> "Paris CDG Tower" in
/// English, "Paris CDG Tour" in French. The OurAirports abbreviations (TWR, GND, APP…) are
/// expanded into words, in the controller's language.
///
/// The AIRPORT NAME itself is never translated: "Frankfurt" stays "Frankfurt" whatever the
/// language — that is what the radio says.
/// </summary>
public static class StationSpeech
{
    public static string Prettify(string? station, string? icao, AtcLanguage lang = AtcLanguage.English)
    {
        if (string.IsNullOrWhiteSpace(station))
            return string.IsNullOrWhiteSpace(icao)
                ? AtcPhrases.Controller(lang, ControllerType.Approach)
                : icao!;

        string s = station.Replace(" · ", " ").Replace("·", " ");
        foreach (var (abbr, word) in AtcPhrases.StationAbbreviations(lang))
            s = Regex.Replace(s, $@"\b{Regex.Escape(abbr)}\b", word, RegexOptions.IgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }
}
