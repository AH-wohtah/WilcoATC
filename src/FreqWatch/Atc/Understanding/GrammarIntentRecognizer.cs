using System.Globalization;
using System.Text.RegularExpressions;

namespace FreqWatch.Atc.Understanding;

/// <summary>
/// Reconnaissance par grammaire / mots-clés, en ANGLAIS (phraséologie OACI).
/// Déterministe, gratuit, hors-ligne.
///
/// TOLÉRANCE (c'est tout l'enjeu quand l'entrée vient d'un micro) :
///  1. le texte passe d'abord par <see cref="AtcTextNormalizer"/> (hésitations, confusions
///     aéro, chiffres épelés) ;
///  2. la correspondance est FLOUE : chaque mot d'un mot-clé est accepté à une faute près
///     (distance de Levenshtein ≤ 1) dès qu'il fait 4 lettres ou plus — « departur »,
///     « pushbak », « clerance » passent. Les mots courts (≤ 3 lettres, « ifr », « to »)
///     restent en correspondance EXACTE, sinon on déclenche sur n'importe quoi ;
///  3. c'est le mot-clé le PLUS LONG qui gagne, pas le premier du tableau. « request taxi,
///     ready for departure » est donc classé « prêt au départ » et pas « roulage ».
/// </summary>
public sealed class GrammarIntentRecognizer : IIntentRecognizer
{
    /// <summary>Longueur minimale d'un mot pour tolérer une faute de frappe/d'écoute.</summary>
    private const int MinLengthForFuzzy = 4;

    private readonly Func<AtcLanguage> _language;

    public GrammarIntentRecognizer(Func<AtcLanguage> language) => _language = language;

    // (intention, mots-clés). Donnés normalisés : minuscules, sans ponctuation.
    private static readonly (PilotIntent Intent, string[] Keywords)[] Table =
    {
        (PilotIntent.ReadyForDeparture, new[]
        {
            "ready for departure", "ready for takeoff", "ready to depart", "ready departure",
            "holding short ready", "ready for the runway", "lined up and ready",
        }),
        (PilotIntent.RequestPushback, new[]
        {
            "request pushback", "pushback", "ready for pushback", "push",
        }),
        (PilotIntent.RequestTaxi, new[]
        {
            "request taxi", "taxi", "ready to taxi", "taxi to the holding point", "taxi to runway",
        }),
        (PilotIntent.RequestClearance, new[]
        {
            "request clearance", "clearance", "ifr clearance", "ifr", "startup", "delivery",
            "cleared to", "request start", "ready to copy clearance",
        }),
        (PilotIntent.ReportApproach, new[]
        {
            "flight level", "inbound", "on approach", "established", "descending",
            "request descent", "with information", "field in sight",
        }),
        (PilotIntent.CheckIn, new[]
        {
            "good day", "good morning", "good afternoon", "good evening", "hello",
            "with you", "check in", "on frequency",
        }),
        (PilotIntent.Readback, new[]
        {
            "roger", "wilco", "copy", "readback", "affirm", "understood", "acknowledged",
        }),
    };

    public Task<RecognizedIntent> RecognizeAsync(string text, CancellationToken ct = default)
    {
        // Nettoyage aéro : c'est lui qui rattrape « push back », « ready for the party »,
        // « one one eight point seven »… avant toute comparaison.
        string clean = AtcTextNormalizer.Normalize(text);
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        PilotIntent best = PilotIntent.Unknown;
        string bestKeyword = "";
        bool bestExact = false;

        foreach (var (intent, keywords) in Table)
        {
            foreach (var kw in keywords)
            {
                var match = Match(words, kw);
                if (match is null) continue;

                // Score = longueur du mot-clé ; à égalité, une correspondance EXACTE
                // l'emporte sur une correspondance approchée.
                bool better = kw.Length > bestKeyword.Length
                              || (kw.Length == bestKeyword.Length && match.Value && !bestExact);
                if (!better) continue;

                best = intent;
                bestKeyword = kw;
                bestExact = match.Value;
            }
        }

        if (best == PilotIntent.Unknown)
            return Task.FromResult(new RecognizedIntent(PilotIntent.Unknown, text, "grammar")
            {
                Reason = string.IsNullOrWhiteSpace(clean)
                    ? "nothing heard"
                    : $"no keyword recognised in “{clean}”",
            });

        string? dest = best == PilotIntent.RequestClearance ? ExtractDestination(text) : null;
        return Task.FromResult(new RecognizedIntent(best, text, "grammar")
        {
            DestinationHint = dest,
            Reason = bestExact ? $"keyword “{bestKeyword}”" : $"keyword “{bestKeyword}” (approximate)",
        });
    }

    /// <summary>
    /// Cherche la suite de mots <paramref name="keyword"/> dans <paramref name="words"/>.
    /// Renvoie null si absente, true si trouvée mot pour mot, false si trouvée à une
    /// faute près sur au moins un mot.
    /// </summary>
    public static bool? Match(string[] words, string keyword)
    {
        var needle = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (needle.Length == 0 || words.Length < needle.Length) return null;

        for (int start = 0; start + needle.Length <= words.Length; start++)
        {
            bool exact = true, ok = true;
            for (int i = 0; i < needle.Length; i++)
            {
                string a = words[start + i], b = needle[i];
                if (a == b) continue;
                if (b.Length >= MinLengthForFuzzy && Levenshtein(a, b) <= 1) { exact = false; continue; }
                ok = false;
                break;
            }
            if (ok) return exact;
        }
        return null;
    }

    /// <summary>Distance d'édition, court-circuitée dès que l'écart dépasse 1.</summary>
    public static int Levenshtein(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return 2; // au-delà du seuil, la valeur exacte est inutile

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin > 1) return 2;  // plus aucune chance de finir ≤ 1
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    // « clearance to dubai please » -> « Dubai ». On travaille sur le texte D'ORIGINE
    // (le nettoyage met tout en minuscules et on veut la casse d'affichage).
    private static string? ExtractDestination(string text)
    {
        var m = Regex.Match(text ?? "", @"\bto\s+(.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        string dest = m.Groups[1].Value;
        // Coupe la politesse / le bruit en fin de phrase.
        dest = Regex.Replace(dest, @"\b(please|thanks?|thank you)\b.*$", "", RegexOptions.IgnoreCase);
        // Au plus 3 mots (les noms de villes/aéroports sont courts).
        dest = string.Join(" ", dest.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries).Take(3)).Trim();

        return dest.Length < 2 ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(dest.ToLowerInvariant());
    }
}
