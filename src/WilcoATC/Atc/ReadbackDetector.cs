using FreqWatch.Common;

namespace FreqWatch.Atc;

/// <summary>
/// Logique PURE (testable) de détection d'un collationnement. Un message est un readback
/// (et non une nouvelle requête) s'il : contient un mot d'accusé de réception, OU reprend
/// ≥ 2 termes significatifs de ce que l'ATC vient de dire, OU n'est rien d'autre que le callsign.
/// </summary>
public static class ReadbackDetector
{
    // Mots d'accusé (normalisés, FR + EN).
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
