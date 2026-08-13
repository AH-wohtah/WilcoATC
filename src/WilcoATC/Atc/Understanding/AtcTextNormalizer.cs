using System.Text;
using System.Text.RegularExpressions;

namespace WilcoATC.Atc.Understanding;

/// <summary>
/// Remet un message pilote BRUT (sortie Whisper ou saisie clavier) en phraséologie
/// exploitable avant la reconnaissance d'intention.
///
/// POURQUOI : Whisper est entraîné sur de la parole générale, pas sur la radio. Il écrit
/// « one one eight point seven », « push back », « taxiing », « ready for the party », et
/// truffe le tout d'hésitations. Sans nettoyage, la grammaire rate des demandes pourtant
/// parfaitement dites — c'est la cause n°1 des « aucun mot-clé reconnu ».
///
/// Trois passes, dans cet ordre (l'ordre compte : on remplace les expressions AVANT de
/// coller les chiffres, sinon « one eight » deviendrait « 18 » dans « runway one eight ») :
///  1. hésitations et bruit de dictée ;
///  2. corrections de vocabulaire aéro (confusions ASR fréquentes) ;
///  3. chiffres épelés -> chiffres, et « point/decimal » -> séparateur décimal.
///
/// Classe PURE (aucune I/O) : entièrement testable.
/// </summary>
public static class AtcTextNormalizer
{
    // --- 1. hésitations : mots vides qui n'apportent rien et cassent les correspondances ---
    private static readonly string[] Fillers =
    {
        "uh", "uhh", "um", "umm", "er", "erm", "ah", "eh", "hmm", "mm",
    };

    // --- 2. confusions ASR fréquentes sur du vocabulaire aéronautique ---
    // Clé = ce que Whisper écrit, valeur = la forme attendue par la grammaire.
    // ATTENTION : expressions entières, comparées sur des mots complets.
    private static readonly (string From, string To)[] Phrases =
    {
        // repoussage
        ("push back", "pushback"),
        ("pushing back", "pushback"),
        ("push-back", "pushback"),
        ("pull back", "pushback"),

        // roulage
        ("taxiing", "taxi"),
        ("taxying", "taxi"),
        ("taxy", "taxi"),
        ("taxi way", "taxiway"),
        ("holding short", "holding short"),
        ("hold short", "holding short"),

        // prêt au départ — Whisper adore transformer « departure » en autre chose
        ("ready for the party", "ready for departure"),
        ("ready for departure", "ready for departure"),
        ("ready for the parcher", "ready for departure"),
        ("ready for take off", "ready for takeoff"),
        ("ready for take-off", "ready for takeoff"),
        ("ready to depart", "ready for departure"),
        ("ready departure", "ready for departure"),
        ("we are ready", "ready"),
        ("we're ready", "ready"),
        ("were ready", "ready"),

        // clairance — « IFR » est le mot que TOUS les moteurs écorchent (mesuré : « if for a »,
        // « if a », « if or »). On ne recolle que devant « clearance », sinon on casserait
        // des « if » légitimes.
        ("clearance delivery", "clearance"),
        ("start up", "startup"),
        ("start-up", "startup"),
        ("i f r", "ifr"),
        ("if for a clearance", "ifr clearance"),
        ("if or clearance", "ifr clearance"),
        ("if a clearance", "ifr clearance"),
        ("i eff are", "ifr"),

        // indicatifs recollés (« speed bird » -> « Speedbird ») : sans effet sur l'intention
        // mais le texte affiché au journal reste lisible.
        ("speed bird", "speedbird"),
        ("speed bud", "speedbird"),

        // collationnement / accusés
        ("will co", "wilco"),
        ("willco", "wilco"),
        ("will comply", "wilco"),
        ("rodger", "roger"),
        ("roger that", "roger"),
        ("affirmative", "affirm"),
        ("read back", "readback"),
        ("copy that", "copy"),

        // report en vol
        ("flight level", "flight level"),
        ("flightlevel", "flight level"),
        ("f l", "flight level"),
        ("in bound", "inbound"),
        ("establish", "established"),

        // politesse / verbes
        ("requesting", "request"),
        ("we request", "request"),
        ("would like to request", "request"),
        ("i would like", "request"),
        ("id like", "request"),
        ("we'd like", "request"),

        // salutations (intention CheckIn)
        ("good day to you", "good day"),
        ("goodday", "good day"),
        ("checking in", "check in"),
        ("checkin", "check in"),
    };

    // --- 3a. chiffres épelés SANS ambiguïté (y compris les formes radio « niner », « tree ») ---
    private static readonly Dictionary<string, string> Digits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = "0", ["oh"] = "0",
        ["one"] = "1", ["wun"] = "1",
        ["two"] = "2",
        ["three"] = "3", ["tree"] = "3",
        ["four"] = "4", ["fower"] = "4",
        ["five"] = "5", ["fife"] = "5",
        ["six"] = "6",
        ["seven"] = "7",
        ["eight"] = "8",
        ["nine"] = "9", ["niner"] = "9",
    };

    /// <summary>
    /// Chiffres HOMOPHONES : ce que la reconnaissance écrit quand elle entend un chiffre
    /// mais choisit le mot courant. « three two zero » ressort en « 3 to 0 », « four four »
    /// en « 4 for ».
    ///
    /// On ne peut PAS les convertir systématiquement : « taxi to the holding point » et
    /// « cleared to Dubai » deviendraient « taxi 2 the… ». Ils ne comptent comme chiffres
    /// que s'ils sont ENTOURÉS de chiffres des deux côtés — « 3 to 0 » oui, « climb to 5000 »
    /// non (le mot précédent n'est pas un chiffre).
    /// </summary>
    private static readonly Dictionary<string, string> AmbiguousDigits = new(StringComparer.OrdinalIgnoreCase)
    {
        ["to"] = "2", ["too"] = "2",
        ["for"] = "4", ["fore"] = "4",
        ["won"] = "1",
        ["ate"] = "8",
        ["free"] = "3",
    };

    /// <summary>
    /// Texte nettoyé, prêt pour la reconnaissance d'intention. Renvoie une chaîne vide
    /// pour une entrée vide (jamais null).
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // Accents retirés AVANT tout le reste : la reconnaissance rend « prêt au décollage »
        // et « steigen auf Flugfläche », alors que les tables de mots-clés sont écrites sans
        // signes diacritiques. Replier des deux côtés évite d'avoir à saisir chaque mot dans
        // ses deux graphies — et rattrape au passage les accents que l'ASR oublie.
        string folded = StripDiacritics(raw!);

        // Minuscules + ponctuation en espaces : la grammaire compare des mots entiers.
        var sb = new StringBuilder(folded.Length);
        foreach (char c in folded)
            sb.Append(char.IsLetterOrDigit(c) || c == '\'' ? char.ToLowerInvariant(c) : ' ');

        string text = Collapse(sb.ToString());
        text = StripFillers(text);
        text = ApplyPhrases(text);
        text = JoinDigits(text);
        text = RestoreFrequencies(text);
        return Collapse(text);
    }

    /// <summary>
    /// Replie les signes diacritiques : « décollage » -> « decollage », « Flugfläche » ->
    /// « Flugflache ». Le ß est traité à part — ce n'est pas un caractère accentué, la
    /// décomposition Unicode le laisse tel quel alors qu'il s'écrit « ss » sans lui.
    /// </summary>
    private static string StripDiacritics(string s)
    {
        string expanded = s.Replace("ß", "ss").Replace("ẞ", "SS");
        string decomposed = expanded.Normalize(NormalizationForm.FormD);

        var sb = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>Mots qui annoncent une FRÉQUENCE juste après (« contact tower on … »).</summary>
    private static readonly string[] FrequencyCues = { "on", "contact", "frequency", "freq", "monitor", "tune" };

    /// <summary>
    /// Rend son point décimal à une fréquence VHF dont la reconnaissance a mangé le
    /// « decimal » : « contact departure on one one eight decimal seven » ressort en
    /// « … on 1187 », qu'on rétablit en « 118.7 ».
    ///
    /// Garde-fous, parce qu'un code transpondeur est AUSSI un nombre de 4 chiffres :
    ///  • le mot précédent doit annoncer une fréquence (donc « squawk 1200 » est épargné) ;
    ///  • les 3 premiers chiffres doivent tomber dans la bande aéronautique 118–137 MHz.
    /// </summary>
    private static string RestoreFrequencies(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 1; i < words.Length; i++)
        {
            string w = words[i];
            if (w.Length is < 4 or > 6 || !w.All(char.IsDigit)) continue;
            if (!FrequencyCues.Contains(words[i - 1], StringComparer.Ordinal)) continue;
            if (!int.TryParse(w[..3], out int mhz) || mhz is < 118 or > 137) continue;

            words[i] = w[..3] + "." + w[3..];
        }
        return string.Join(' ', words);
    }

    private static string StripFillers(string text)
    {
        var kept = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                       .Where(w => !Fillers.Contains(w, StringComparer.Ordinal));
        return string.Join(' ', kept);
    }

    private static string ApplyPhrases(string text)
    {
        string padded = " " + text + " ";
        foreach (var (from, to) in Phrases)
        {
            string needle = " " + from + " ";
            if (from == to) continue;                       // entrée présente pour mémoire
            while (padded.Contains(needle))
                padded = padded.Replace(needle, " " + to + " ");
        }
        return padded.Trim();
    }

    /// <summary>
    /// « one one eight point seven » -> « 118.7 », « runway two seven » -> « runway 27 »,
    /// « level 3 to 0 » -> « level 320 ».
    ///
    /// Les chiffres CONSÉCUTIFS sont collés (phraséologie : on épelle chiffre par chiffre) ;
    /// « point »/« decimal » entre deux groupes devient un vrai point décimal ; et les
    /// homophones (<see cref="AmbiguousDigits"/>) sont résolus AVANT le collage, uniquement
    /// quand leurs deux voisins sont des chiffres.
    /// </summary>
    private static string JoinDigits(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Passe 1 : chiffre certain ? (mot-chiffre non ambigu ou nombre déjà écrit en chiffres)
        var isDigit = new bool[words.Length];
        for (int i = 0; i < words.Length; i++)
            isDigit[i] = Digits.ContainsKey(words[i])
                         || (words[i].Length > 0 && words[i].All(char.IsDigit));

        // Passe 2 : un homophone ENTOURÉ de chiffres en est un aussi.
        var resolved = new string[words.Length];
        for (int i = 0; i < words.Length; i++)
        {
            resolved[i] = words[i];
            if (isDigit[i] || !AmbiguousDigits.TryGetValue(words[i], out var val)) continue;
            if (i > 0 && isDigit[i - 1] && i + 1 < words.Length && isDigit[i + 1])
            {
                resolved[i] = val;
                isDigit[i] = true;
            }
        }

        // Passe 3 : collage des suites de chiffres.
        var outWords = new List<string>();
        var run = new StringBuilder();
        void Flush() { if (run.Length > 0) { outWords.Add(run.ToString()); run.Clear(); } }

        for (int i = 0; i < resolved.Length; i++)
        {
            string w = resolved[i];

            if (isDigit[i]) { run.Append(Digits.TryGetValue(w, out var d) ? d : w); continue; }

            // « point » / « decimal » ne compte que ENTRE deux groupes de chiffres.
            bool isSeparator = w is "point" or "decimal";
            if (isSeparator && run.Length > 0 && i + 1 < resolved.Length && isDigit[i + 1])
            {
                run.Append('.');
                continue;
            }

            Flush();
            outWords.Add(w);
        }
        Flush();

        return string.Join(' ', outWords);
    }

    private static string Collapse(string s)
        => Regex.Replace(s, @"\s+", " ").Trim();
}
