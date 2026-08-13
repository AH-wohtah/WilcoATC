using System.Globalization;
using System.Text.RegularExpressions;

namespace WilcoATC.Atc.Understanding;

/// <summary>
/// Reconnaissance par grammaire / mots-clés. Déterministe, gratuite, hors-ligne.
///
/// MULTILINGUE : le contrôleur comprend TOUTES les langues gérées, quelle que soit celle
/// dans laquelle il répond. Chaque table de <see cref="IntentKeywords"/> est essayée, et la
/// meilleure correspondance gagne — elle donne à la fois l'intention ET la langue du pilote
/// (<see cref="RecognizedIntent.Language"/>), sur laquelle le contrôleur s'aligne ensuite.
/// La langue courante n'est utilisée que pour DÉPARTAGER deux correspondances de même
/// force : à égalité, on croit d'abord la langue dans laquelle on vient de parler.
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
    private readonly SpokenCallsignResolver? _callsigns;

    /// <param name="callsigns">
    /// Optionnel. Fourni, il recolle les télophonies que la reconnaissance a séparées
    /// (« speed bird 123 » -> « speedbird 123 ») et renseigne
    /// <see cref="RecognizedIntent.CallsignHint"/>. Absent, tout le reste fonctionne
    /// à l'identique — pratique pour les bancs d'essai.
    /// </param>
    public GrammarIntentRecognizer(Func<AtcLanguage> language, SpokenCallsignResolver? callsigns = null)
    {
        _language = language;
        _callsigns = callsigns;
    }

    public Task<RecognizedIntent> RecognizeAsync(string text, CancellationToken ct = default)
    {
        // Nettoyage aéro : c'est lui qui rattrape « push back », « ready for the party »,
        // « one one eight point seven »… avant toute comparaison.
        string clean = AtcTextNormalizer.Normalize(text);

        // Passe compagnies : elle DOIT venir après la normalisation (elle a besoin des
        // chiffres déjà collés pour reconnaître « télophonie + numéro ») et avant la
        // comparaison des mots-clés.
        if (_callsigns is not null) clean = _callsigns.Canonicalize(clean);
        string? callsignHint = _callsigns?.Find(clean)?.Display;

        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        PilotIntent best = PilotIntent.Unknown;
        string bestKeyword = "";
        bool bestExact = false;
        AtcLanguage bestLanguage = AtcLanguage.English;

        // ANGLAIS UNIQUEMENT POUR L'INSTANT : on n'essaie plus qu'une table. Les autres
        // langues restent écrites dans IntentKeywords et n'attendent que le retour du
        // multilingue — il suffira de rendre son corps à LanguagesToTry().
        foreach (var language in LanguagesToTry())
        {
            foreach (var (intent, keywords) in IntentKeywords.For(language))
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
                    bestLanguage = language;
                }
            }
        }

        if (best == PilotIntent.Unknown)
            return Task.FromResult(new RecognizedIntent(PilotIntent.Unknown, text, "grammar")
            {
                CallsignHint = callsignHint,
                Reason = string.IsNullOrWhiteSpace(clean)
                    ? "nothing heard"
                    : $"no keyword recognised in “{clean}”",
            });

        string? dest = best == PilotIntent.RequestClearance ? ExtractDestination(text) : null;
        return Task.FromResult(new RecognizedIntent(best, text, "grammar")
        {
            DestinationHint = dest,
            CallsignHint = callsignHint,
            Language = bestLanguage,
            Reason = (bestExact ? $"keyword “{bestKeyword}”" : $"keyword “{bestKeyword}” (approximate)")
                     + $" [{bestLanguage.Code()}]",
        });
    }

    /// <summary>
    /// Langues essayées. ANGLAIS SEUL pour l'instant : la compréhension multilingue est
    /// coupée. Le retour se fait ici — langue courante d'abord, puis toutes les autres :
    /// <code>
    /// var current = _language();
    /// yield return current;
    /// foreach (var l in AtcLanguages.All) if (l != current) yield return l;
    /// </code>
    /// </summary>
    private IEnumerable<AtcLanguage> LanguagesToTry()
    {
        yield return AtcLanguage.English;
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

    /// <summary>
    /// Mots qui ANNONCENT la destination. Le pilote ne dit pas toujours « to » — en Europe,
    /// la tournure courante est « request startup, DESTINATION Charles de Gaulle ».
    ///
    /// L'ancienne version n'acceptait que « to … » ET prenait tout jusqu'à la fin de la
    /// phrase. Une transmission complète et parfaitement standard — « Brussels delivery,
    /// good day, Beeline 3633, Airbus A320, gate 154, request startup destination Charles de
    /// Gaulle, information Delta » — n'y correspondait donc pas du tout : le contrôleur
    /// répondait « say again your destination » et ne délivrait jamais la clairance.
    /// </summary>
    private static readonly string[] DestinationCues =
    {
        "destination", "inbound to", "bound for", "to", "for",
    };

    /// <summary>
    /// Mots qui FERMENT la destination. Une transmission réelle continue après elle : le
    /// code ATIS (« information Delta »), le niveau demandé, le poste de stationnement.
    /// Sans ces bornes, « Charles de Gaulle information Delta » partait en entier comme nom
    /// d'aéroport.
    /// </summary>
    private static readonly string[] DestinationStops =
    {
        "information", "with information", "gate", "stand", "stand number", "squawk",
        "flight level", "level", "runway", "please", "thanks", "thank you", "on stand",
        "request", "atis", "qnh",
    };

    // « clearance to dubai please » -> « Dubai ». On travaille sur le texte D'ORIGINE
    // (le nettoyage met tout en minuscules et on veut la casse d'affichage).
    private static string? ExtractDestination(string text)
    {
        string source = text ?? "";

        foreach (string cue in DestinationCues)
        {
            // « to » ne doit pas mordre dans « request startup » ni « ready to copy » : on
            // exige une frontière de mot des deux côtés.
            var m = Regex.Match(source, $@"\b{Regex.Escape(cue)}\s+(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;

            string dest = m.Groups[1].Value;

            // On coupe au premier mot de clôture : c'est lui qui marque la fin du nom.
            foreach (string stop in DestinationStops)
            {
                var s = Regex.Match(dest, $@"\b{Regex.Escape(stop)}\b", RegexOptions.IgnoreCase);
                if (s.Success) dest = dest[..s.Index];
            }

            // Au plus 4 mots : « Charles de Gaulle » en fait trois, « Paris Charles de
            // Gaulle » quatre. Au-delà, ce n'est plus un nom d'aéroport.
            dest = string.Join(" ", dest
                .Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Take(4)).Trim();

            if (dest.Length >= 2)
                return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(dest.ToLowerInvariant());
        }

        return null;
    }
}
