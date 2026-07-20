using System.Globalization;
using System.Text.RegularExpressions;
using FreqWatch.Common;

namespace FreqWatch.Atc.Understanding;

/// <summary>
/// Reconnaissance par grammaire / mots-clés, BILINGUE et pilotée par la langue effective
/// (réglage « Langue des transmissions »). Déterministe, gratuit, hors-ligne.
///
/// Tolérance : on NORMALISE le texte (minuscules, accents supprimés, ponctuation → espace,
/// espaces compressés), puis on cherche des mots-clés en tokens entiers — ce qui ignore la
/// casse, la ponctuation, les hésitations et le callsign autour de la formulation.
/// L'ordre du tableau = priorité (« prêt au départ » avant « roulage », etc.).
/// </summary>
public sealed class GrammarIntentRecognizer : IIntentRecognizer
{
    private readonly Func<AtcLanguage> _language;

    public GrammarIntentRecognizer(Func<AtcLanguage> language) => _language = language;

    // (intention, mots-clés EN, mots-clés FR). Les mots-clés sont donnés SANS accents
    // (le texte est normalisé pareil), en tokens entiers.
    private static readonly (PilotIntent Intent, string[] En, string[] Fr)[] Table =
    {
        (PilotIntent.ReadyForDeparture,
            new[] { "ready for departure", "ready for take", "ready to depart", "holding short ready", "ready departure" },
            new[] { "pret au depart", "prets au depart", "pret au decollage", "prets au decollage", "pret pour le depart" }),
        (PilotIntent.RequestPushback,
            new[] { "pushback", "push back", "push" },
            new[] { "repoussage", "repousser", "repousse", "demandons le repoussage", "pushback", "push" }),
        (PilotIntent.RequestTaxi,
            new[] { "taxi" },
            new[] { "roulage", "rouler", "demandons le roulage" }),
        (PilotIntent.RequestClearance,
            new[] { "clearance", "ifr", "startup", "start up", "delivery", "cleared to" },
            new[] { "clairance", "autorisation de depart", "demandons la clairance", "plan de vol depose" }),
        (PilotIntent.ReportApproach,
            new[] { "flight level", "inbound", "on approach", "established", "descending" },
            new[] { "niveau de vol", "en approche", "en descente", "etabli", "etablis", "au niveau" }),
        (PilotIntent.CheckIn,
            new[] { "good day", "good morning", "good evening", "hello", "with you", "check in", "checking in" },
            new[] { "bonjour", "bonsoir" }),
        (PilotIntent.Readback,
            new[] { "roger", "wilco", "copy", "readback", "read back", "affirm" },
            new[] { "bien recu", "recu", "roger", "wilco", "compris", "affirme" }),
    };

    public Task<RecognizedIntent> RecognizeAsync(string text, CancellationToken ct = default)
    {
        bool fr = _language() == AtcLanguage.French;
        string padded = " " + TextUtil.Normalize(text) + " ";

        foreach (var (intent, en, frk) in Table)
        {
            foreach (var kw in fr ? frk : en)
            {
                if (padded.Contains(" " + TextUtil.Normalize(kw) + " "))
                {
                    string? dest = intent == PilotIntent.RequestClearance ? ExtractDestination(text, fr) : null;
                    return Task.FromResult(new RecognizedIntent(intent, text, "grammar")
                    {
                        DestinationHint = dest,
                        Reason = $"mot-clé « {kw} »",
                    });
                }
            }
        }

        return Task.FromResult(new RecognizedIntent(PilotIntent.Unknown, text, "grammar")
        {
            Reason = $"aucun mot-clé reconnu (langue={(fr ? "FR" : "EN")})",
        });
    }

    // « clearance to dubai » / « clairance pour dubaï s'il vous plaît » -> « Dubaï ».
    private static string? ExtractDestination(string text, bool fr)
    {
        string pattern = fr ? @"\b(?:pour|vers|à destination de)\s+(.+)$" : @"\bto\s+(.+)$";
        var m = Regex.Match(text ?? "", pattern, RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        string dest = m.Groups[1].Value;
        // Coupe la politesse / le bruit en fin de phrase.
        dest = Regex.Replace(dest, @"\b(s'?il vous pla[iî]t|svp|please|merci|thanks?|thank you)\b.*$", "",
            RegexOptions.IgnoreCase);
        // Au plus 3 mots (les noms de villes/aéroports sont courts).
        dest = string.Join(" ", dest.Split(new[] { ' ', ',', '.' }, StringSplitOptions.RemoveEmptyEntries).Take(3)).Trim();

        return dest.Length < 2 ? null : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(dest.ToLowerInvariant());
    }
}
