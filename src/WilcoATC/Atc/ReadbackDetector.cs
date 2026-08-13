using WilcoATC.Common;

namespace WilcoATC.Atc;

/// <summary>
/// PURE (therefore testable) logic for spotting a read-back. A message is a read-back (rather
/// than a new request) when it: contains an acknowledgement word, OR repeats >= 2 meaningful
/// terms of what the controller just said, OR is nothing but the callsign.
/// </summary>
public static class ReadbackDetector
{
    // Acknowledgement words (normalised, FR + EN).
    private static readonly HashSet<string> AckWords = new(StringComparer.Ordinal)
    {
        "approuve", "approuvee", "approuves", "correct", "roger", "wilco", "recu", "recus",
        "collationne", "collationnement", "compris", "copy", "copied", "affirm", "affirme", "ok",
    };

    public static bool IsReadback(string message, string callsign, string atcWords)
    {
        var msg = TextUtil.Tokenize(message);
        if (msg.Length == 0) return false;

        if (msg.Any(t => AckWords.Contains(t))) return true;

        var atc = new HashSet<string>(TextUtil.Tokenize(atcWords).Where(t => t.Length >= 3));
        if (msg.Count(t => t.Length >= 3 && atc.Contains(t)) >= 2) return true;

        var call = new HashSet<string>(TextUtil.Tokenize(callsign));
        if (call.Count > 0 && msg.All(t => call.Contains(t))) return true;

        return false;
    }
}
