using System.Text.RegularExpressions;
using WilcoATC.Atc.Understanding;
using WilcoATC.Common;

namespace WilcoATC.Atc;

/// <summary>Nature d'un élément à collationner. Sert à formuler le rappel.</summary>
public enum ReadbackItemKind
{
    Runway,
    Squawk,
    Frequency,
    Altitude,
}

/// <summary>Un élément OBLIGATOIRE du collationnement : sa nature et les chiffres attendus.</summary>
/// <param name="Digits">Chiffres normalisés, sans espaces (« 26 », « 4271 », « 118.7 »).</param>
public sealed record ReadbackItem(ReadbackItemKind Kind, string Digits);

/// <summary>
/// Vérifie qu'un collationnement REPREND réellement l'instruction.
///
/// POURQUOI CE N'EST PAS <see cref="ReadbackDetector"/> : celui-ci répond à « est-ce un
/// collationnement plutôt qu'une nouvelle requête ? » et se contente d'un « roger ». Ici on
/// répond à « le pilote a-t-il répété ce qu'il DEVAIT répéter ? » — ce qui est le sens même
/// du collationnement : la piste, le squawk, la fréquence et l'altitude se relisent, sous
/// peine que le contrôleur reprenne l'instruction.
///
/// MÉTHODE : les phrases de l'ATC sont produites par l'application, donc on connaît les mots
/// qui annoncent une valeur (« piste », « squawk », « niveau de vol »…). On repère ces mots
/// dans CE QUE L'ATC VIENT DE DIRE, on prend les chiffres qui suivent, et on exige de les
/// retrouver dans la réponse du pilote. Tout est comparé sur du texte normalisé (sans
/// accents, sans ponctuation), donc la langue ne change rien à la mécanique.
///
/// TOLÉRANT SUR LA FORME : « deux six » n'est pas exigé, seuls les CHIFFRES comptent, et
/// « 2 6 » vaut « 26 » — la reconnaissance vocale colle ou sépare les chiffres au hasard.
/// Le QNH n'est PAS exigé : il se lit dans la vraie vie, mais l'exiger transformerait
/// chaque roulage en interrogation écrite.
/// </summary>
public static class ReadbackChecker
{
    // Mots qui ANNONCENT une valeur, dans les cinq langues produites par AtcPhrases.
    // Donnés normalisés (minuscules, sans accents) : c'est sous cette forme qu'on compare.
    private static readonly (string Word, ReadbackItemKind Kind)[] Anchors =
    {
        ("runway", ReadbackItemKind.Runway),
        ("piste", ReadbackItemKind.Runway),
        ("pista", ReadbackItemKind.Runway),
        ("squawk", ReadbackItemKind.Squawk),
        ("level", ReadbackItemKind.Altitude),      // flight level
        ("niveau", ReadbackItemKind.Altitude),
        ("flugflache", ReadbackItemKind.Altitude), // Flugfläche, accents repliés
        ("nivel", ReadbackItemKind.Altitude),
        ("livello", ReadbackItemKind.Altitude),
        ("feet", ReadbackItemKind.Altitude),
        ("pieds", ReadbackItemKind.Altitude),
        ("fuss", ReadbackItemKind.Altitude),       // Fuß -> « fuss » après repli
        ("pies", ReadbackItemKind.Altitude),
        ("piedi", ReadbackItemKind.Altitude),
    };

    // Les unités d'altitude SUIVENT le nombre (« 5000 feet »), contrairement aux autres
    // ancres qui le précèdent (« runway 26 »). On les traite donc à rebours.
    private static readonly HashSet<string> TrailingAnchors = new(StringComparer.Ordinal)
    {
        "feet", "pieds", "fuss", "pies", "piedi",
    };

    /// <summary>
    /// Éléments que le pilote DOIT relire, extraits de la transmission de l'ATC.
    /// Liste vide = rien d'obligatoire (une salutation, un « restez sur cette fréquence »…).
    /// </summary>
    public static IReadOnlyList<ReadbackItem> Required(string? atcText)
    {
        var items = new List<ReadbackItem>();
        if (string.IsNullOrWhiteSpace(atcText)) return items;

        // On garde les points : ils séparent les mégahertz d'une fréquence (118.7).
        string text = NormalizeAtc(atcText!);
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < words.Length; i++)
        {
            var kind = KindOf(words[i]);
            if (kind is null) continue;

            string digits = TrailingAnchors.Contains(words[i])
                ? DigitsBefore(words, i)
                : DigitsAfter(words, i);

            if (digits.Length > 0) Add(items, new ReadbackItem(kind.Value, digits));
        }

        // La fréquence d'un transfert n'a pas de mot d'annonce commun aux cinq langues
        // (« on », « sur », « auf »…). On la reconnaît à sa FORME : un nombre à décimale
        // dans la bande aéronautique. C'est l'élément dont l'oubli coûte le plus cher.
        // 118 à 136 : TOUTE la bande aviation. Le motif précédent commençait à 12x et ratait
        // donc 118 et 119 — c'est-à-dire les fréquences de tour et de sol les plus répandues.
        // Aucune fréquence n'était alors exigée, et l'oublier ne coûtait rien au pilote.
        foreach (Match m in Regex.Matches(text, @"\b1(1[89]|2[0-9]|3[0-6])\.[0-9]{1,3}\b"))
            Add(items, new ReadbackItem(ReadbackItemKind.Frequency, m.Value));

        return items;
    }

    /// <summary>
    /// Éléments MANQUANTS dans la réponse du pilote. Vide = collationnement complet.
    /// </summary>
    public static IReadOnlyList<ReadbackItem> Missing(string? pilotText, IReadOnlyList<ReadbackItem> required)
    {
        if (required.Count == 0) return Array.Empty<ReadbackItem>();

        // Chiffres du pilote, espaces retirés : « deux six » ne compte pas, mais « 2 6 »
        // et « 26 » sont la même chose — la reconnaissance vocale tranche au hasard.
        string spoken = ExpandMagnitudes(Normalize(pilotText)).Replace(" ", "");

        return required.Where(r => !spoken.Contains(r.Digits, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// Développe les ORDRES DE GRANDEUR : « 5 thousand » -> « 5000 », « 2 thousand 5 hundred »
    /// -> « 2500 », « 3 hundred » -> « 300 ».
    ///
    /// POURQUOI ICI ET PAS DANS LE NORMALISEUR GÉNÉRAL : une altitude se PRONONCE ainsi — un
    /// pilote dit « climbing five thousand feet », jamais « five zero zero zero ». Le
    /// vérificateur, lui, attend les chiffres de l'ATC (« 5000 ») et ne trouvait donc jamais
    /// son compte. Le développement reste cantonné au collationnement : l'appliquer à la
    /// reconnaissance d'intention risquerait de transformer des tournures qui n'ont rien de
    /// numérique, pour un gain nul de ce côté.
    ///
    /// Les autres langues disent « mille » et « cent » — non traités ici, l'ATC étant
    /// anglophone. À compléter le jour où le multilingue reviendra.
    /// </summary>
    private static string ExpandMagnitudes(string text)
    {
        // « 2 thousand 5 hundred » d'abord : sinon la règle du millier seul le couperait en deux.
        text = Regex.Replace(text, @"\b(\d{1,2}) thousand (\d{1,2}) hundred\b",
            m => (int.Parse(m.Groups[1].Value) * 1000 + int.Parse(m.Groups[2].Value) * 100).ToString());

        text = Regex.Replace(text, @"\b(\d{1,2}) thousand\b",
            m => (int.Parse(m.Groups[1].Value) * 1000).ToString());

        text = Regex.Replace(text, @"\b(\d{1,2}) hundred\b",
            m => (int.Parse(m.Groups[1].Value) * 100).ToString());

        return text;
    }

    private static ReadbackItemKind? KindOf(string word)
    {
        foreach (var (w, kind) in Anchors)
            if (word == w) return kind;
        return null;
    }

    /// <summary>
    /// Chiffres qui suivent l'ancre, éventuellement épelés (« 2 6 »).
    ///
    /// Jusqu'à <see cref="MaxFillerWords"/> mots peuvent s'intercaler : l'ancre est un seul
    /// mot, mais la tournure ne l'est pas — « niveau DE VOL 250 », « nivel DE VUELO 250 »,
    /// « livello DI VOLO 250 ». Au-delà, on renonce plutôt que d'attraper le nombre d'une
    /// autre partie de la phrase : « la piste en service, QNH 1013 » ne doit pas exiger de
    /// relire « 1013 » comme s'il s'agissait d'un numéro de piste.
    /// </summary>
    private const int MaxFillerWords = 3;

    private static string DigitsAfter(string[] words, int anchor)
    {
        var sb = new System.Text.StringBuilder();

        for (int i = anchor + 1; i < words.Length; i++)
        {
            if (IsNumber(words[i])) { sb.Append(words[i]); continue; }
            if (sb.Length > 0) break;                       // la suite de chiffres est terminée
            if (i - anchor > MaxFillerWords) break;         // trop loin de l'ancre -> ce n'est pas sa valeur
        }
        return sb.ToString();
    }

    /// <summary>Chiffres qui précèdent l'ancre (« 5000 feet »).</summary>
    private static string DigitsBefore(string[] words, int anchor)
    {
        var parts = new List<string>();
        for (int i = anchor - 1; i >= 0 && i >= anchor - 6; i--)
        {
            if (IsNumber(words[i])) parts.Insert(0, words[i]);
            else break;
        }
        return string.Concat(parts);
    }

    private static bool IsNumber(string w) => w.Length > 0 && w.All(c => char.IsDigit(c) || c == '.');

    private static void Add(List<ReadbackItem> items, ReadbackItem item)
    {
        if (!items.Any(i => i.Kind == item.Kind && i.Digits == item.Digits)) items.Add(item);
    }

    /// <summary>
    /// Minuscules, accents repliés, ponctuation en espaces — comme
    /// <see cref="TextUtil.Normalize"/>, à une exception près : le POINT DÉCIMAL est
    /// conservé, sans quoi « 118.7 » se casserait en « 118 » et « 7 », et la fréquence
    /// deviendrait invérifiable.
    /// </summary>
    /// <summary>
    /// Normalisation du texte, PARTAGÉE avec la reconnaissance d'intention.
    ///
    /// LE DÉFAUT QU'ELLE CORRIGE. Ce vérificateur avait sa propre normalisation, qui repliait
    /// les accents et la ponctuation mais NE CONVERTISSAIT PAS les nombres écrits en toutes
    /// lettres. Or l'ATC dit « runway 2 7 » — des chiffres — tandis que la reconnaissance
    /// vocale transcrit ce que le pilote prononce : « runway two seven ». La comparaison
    /// cherchait donc « 27 » dans « runwaytwoseven » et échouait À CHAQUE FOIS : quoi que
    /// dise le pilote, le collationnement était déclaré incomplet.
    ///
    /// <see cref="AtcTextNormalizer"/> fait déjà ce travail, et bien mieux : chiffres épelés,
    /// formes radio (« niner », « tree »), homophones de la reconnaissance (« 3 to 0 » pour
    /// « three two zero ») et reconstruction des fréquences. C'est elle qui permettait à la
    /// reconnaissance d'intention de fonctionner pendant que le collationnement échouait —
    /// deux normalisations pour un même texte, dont une seule était à jour.
    /// </summary>
    private static string Normalize(string? s) => AtcTextNormalizer.Normalize(s);

    /// <summary>
    /// Ancienne normalisation, CONSERVÉE pour le seul cas qu'elle traitait mieux : le texte de
    /// l'ATC, où le point décimal d'une fréquence est déjà écrit (« 118.7 ») et doit survivre.
    /// </summary>
    private static string NormalizeAtc(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";

        string formD = s!.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(formD.Length);

        for (int i = 0; i < formD.Length; i++)
        {
            char c = formD[i];
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                == System.Globalization.UnicodeCategory.NonSpacingMark) continue;

            // Point conservé UNIQUEMENT entre deux chiffres : celui d'une fin de phrase,
            // lui, doit rester un séparateur.
            bool decimalPoint = c == '.' && i > 0 && i + 1 < formD.Length
                                && char.IsDigit(formD[i - 1]) && char.IsDigit(formD[i + 1]);

            sb.Append(char.IsLetterOrDigit(c) || decimalPoint ? c : ' ');
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
