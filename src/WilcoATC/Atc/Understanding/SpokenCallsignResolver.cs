using WilcoATC.Formatting;

namespace WilcoATC.Atc.Understanding;

/// <summary>Indicatif de compagnie repéré dans une transmission pilote.</summary>
/// <param name="Icao">Code OACI de la compagnie (ex. « BAW »).</param>
/// <param name="Telephony">Télophonie parlée (ex. « Speedbird »).</param>
/// <param name="Number">Numéro de vol tel que prononcé (ex. « 123 »), ou null.</param>
public sealed record SpokenCallsign(string Icao, string Telephony, string? Number)
{
    public string Display => Number is null ? Telephony : $"{Telephony} {Number}";
}

/// <summary>
/// Reconnaît une TÉLOPHONIE DE COMPAGNIE prononcée par le pilote, à partir de la même base
/// que celle qui sert à la générer (<see cref="AirlineTelephony"/>) — la base est donc
/// utilisée dans les DEUX SENS.
///
/// POURQUOI PAS DES HOTWORDS : c'est l'approche qui avait été essayée côté moteur ASR, et
/// elle dégradait le résultat (10,5 % contre 8,8 % de WER) — le vocabulaire du modèle est
/// en sous-mots, les mots entiers ne s'y encodent pas. La correction APRÈS transcription n'a
/// pas ce défaut : elle ne touche pas au décodage, elle répare sa sortie.
///
/// Deux difficultés réelles, traitées ici :
///  • la reconnaissance SÉPARE les télophonies collées (« speedbird » -> « speed bird ») ;
///  • elle les écorche d'une lettre (« speedbird » -> « speedbird »/« speedbard »).
///
/// GARDE-FOU : une télophonie n'est retenue que si elle est SUIVIE D'UN NOMBRE. Sans ça,
/// des entrées du dataset qui sont des mots courants (« Cactus », « Eagle », « Brickyard »)
/// se déclencheraient sur de la phraséologie ordinaire. Un indicatif, c'est une compagnie
/// ET un numéro.
///
/// Logique PURE une fois la base chargée : testable sans micro.
/// </summary>
public sealed class SpokenCallsignResolver
{
    /// <summary>Nombre maximal de mots qu'une télophonie peut occuper (« Air Canada » = 2).</summary>
    private const int MaxWords = 3;

    /// <summary>En deçà, une télophonie est trop courte pour être distinguée sans risque.</summary>
    private const int MinLength = 4;

    private readonly AirlineTelephony _airlines;
    private readonly object _gate = new();

    // Clé = télophonie sans espaces ni casse (« aircanada ») -> (ICAO, forme affichable).
    private Dictionary<string, (string Icao, string Display)>? _index;

    public SpokenCallsignResolver(AirlineTelephony airlines) => _airlines = airlines;

    private Dictionary<string, (string Icao, string Display)> Index()
    {
        lock (_gate)
        {
            if (_index is not null) return _index;

            var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var (icao, telephony) in _airlines.All())
            {
                string key = Squash(telephony);
                if (key.Length < MinLength) continue;
                // Première occurrence gagnante : le dataset contient des doublons de télophonie.
                if (!map.ContainsKey(key)) map[key] = (icao, telephony);
            }
            _index = map;
            return map;
        }
    }

    /// <summary>
    /// Cherche « télophonie + numéro » dans un texte DÉJÀ normalisé par
    /// <see cref="AtcTextNormalizer"/> (minuscules, chiffres collés). Renvoie null si rien
    /// d'exploitable — on ne devine jamais une compagnie.
    /// </summary>
    public SpokenCallsign? Find(string? normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText)) return null;

        var words = normalizedText!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = Index();

        for (int start = 0; start < words.Length; start++)
        {
            // Les groupes LONGS d'abord : « air canada » doit gagner sur « air ».
            for (int len = Math.Min(MaxWords, words.Length - start); len >= 1; len--)
            {
                int after = start + len;
                if (after >= words.Length) continue;          // il FAUT un mot derrière…
                string number = words[after];
                if (!IsFlightNumber(number)) continue;        // …et ce doit être un numéro

                string candidate = Squash(string.Concat(words.Skip(start).Take(len)));
                if (candidate.Length < MinLength) continue;

                if (index.TryGetValue(candidate, out var hit))
                    return new SpokenCallsign(hit.Icao, hit.Display, number);

                // Écorchure d'une lettre, seulement sur les formes assez longues pour que
                // ce soit sans danger (« swiss »/« swiff » oui, « air »/« aim » non).
                if (candidate.Length < 6) continue;
                foreach (var (key, value) in index)
                {
                    if (Math.Abs(key.Length - candidate.Length) > 1) continue;
                    if (GrammarIntentRecognizer.Levenshtein(candidate, key) > 1) continue;
                    return new SpokenCallsign(value.Icao, value.Display, number);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Réécrit le texte pour que la télophonie reconnue apparaisse sous sa forme CANONIQUE
    /// et en un seul mot. « speed bird 123 wilco » -> « speedbird 123 wilco ». Le
    /// collationnement compare l'indicatif attendu au texte entendu : sans ça, un indicatif
    /// coupé en deux ne matchait jamais.
    /// </summary>
    public string Canonicalize(string? normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText)) return normalizedText ?? "";

        var words = normalizedText!.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var index = Index();

        for (int start = 0; start < words.Count; start++)
        {
            for (int len = Math.Min(MaxWords, words.Count - start); len >= 2; len--)
            {
                int after = start + len;
                if (after >= words.Count || !IsFlightNumber(words[after])) continue;

                string candidate = Squash(string.Concat(words.Skip(start).Take(len)));
                if (candidate.Length < MinLength || !index.ContainsKey(candidate)) continue;

                // Remplace les `len` mots par la forme collée, puis on repart de cet index.
                words.RemoveRange(start, len);
                words.Insert(start, candidate);
                break;
            }
        }
        return string.Join(' ', words);
    }

    /// <summary>Un numéro de vol : 1 à 4 chiffres, éventuellement suivis d'une lettre.</summary>
    private static bool IsFlightNumber(string w)
    {
        if (w.Length is < 1 or > 5) return false;
        int digits = 0;
        for (int i = 0; i < w.Length; i++)
        {
            if (char.IsDigit(w[i])) { digits++; continue; }
            if (i == w.Length - 1 && char.IsLetter(w[i])) continue; // « 123a »
            return false;
        }
        return digits is >= 1 and <= 4;
    }

    /// <summary>Minuscules, sans espaces ni tirets : « Air Canada » -> « aircanada ».</summary>
    private static string Squash(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}
