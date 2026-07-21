using FreqWatch.Atc;

namespace FreqWatch.Audio;

/// <summary>
/// Attribue une voix STABLE et distincte à une « identité » parlante : un contrôleur
/// (nom de station / fréquence), un équipage d'ambiance (indicatif), etc.
///
/// RÈGLE ESSENTIELLE : la voix doit correspondre à la LANGUE du texte. On ne fait jamais
/// lire de l'anglais par un modèle français (prononciation catastrophique) : les voix sont
/// d'abord filtrées sur la langue, puis on tire dedans.
///
/// Le tirage est déterministe (hachage FNV-1a, pas <c>string.GetHashCode</c> qui varie d'un
/// lancement à l'autre) : le même contrôleur garde la même voix pendant tout le vol, et
/// changer de fréquence change bien de voix.
///
/// Sources de variété, par ordre de force :
///  1. plusieurs voix installées DANS LA BONNE LANGUE -> on en choisit une différente ;
///  2. modèle multi-locuteurs -> on change de locuteur (idéal : libritts_r, vctk) ;
///  3. dans tous les cas -> léger écart de débit.
/// </summary>
public sealed class VoicePicker
{
    private readonly VoiceRepository _voices;

    public VoicePicker(VoiceRepository voices) => _voices = voices;

    /// <summary>Voix attribuée à une identité, dans la langue demandée.</summary>
    public TtsVoice For(string? identity, AtcLanguage language)
    {
        string wanted = language switch { _ => "en" };   // une seule langue pour l'instant
        var all = _voices.List().Select(v => v.Name).ToList();
        if (all.Count == 0) return TtsVoice.Default;   // rien d'installé -> repli du moteur

        // 1) Voix de la bonne langue. 2) À défaut, voix de langue inconnue (modèles exotiques).
        //    3) En dernier recours seulement, n'importe quoi : mieux vaut parler que se taire.
        var pool = all.Where(n => LanguageOf(n) == wanted).ToList();
        if (pool.Count == 0) pool = all.Where(n => LanguageOf(n) is null).ToList();
        if (pool.Count == 0) pool = all;

        uint h = string.IsNullOrWhiteSpace(identity) ? 0u : Fnv1a(identity!);

        string name = pool[(int)(h % (uint)pool.Count)];
        int speaker = (int)((h >> 8) % 64);              // borné par le moteur selon le modèle
        float speed = 0.94f + (h >> 16) % 13 * 0.01f;    // 0.94 .. 1.06

        return new TtsVoice(name, speaker, speed);
    }

    /// <summary>
    /// Langue d'un modèle d'après son nom (« vits-piper-en_GB-alan-medium » -> « en »),
    /// ou null si le nom ne suit pas la convention Piper.
    /// </summary>
    public static string? LanguageOf(string? voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName)) return null;

        foreach (var part in voiceName!.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            // On cherche un segment de type « en_US », « fr_FR », « de_DE »…
            if (part.Length >= 5 && part[2] == '_'
                && char.IsLetter(part[0]) && char.IsLetter(part[1])
                && char.IsLetter(part[3]) && char.IsLetter(part[4]))
            {
                return part[..2].ToLowerInvariant();
            }
        }
        return null;
    }

    private static uint Fnv1a(string s)
    {
        uint hash = 2166136261;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }
}
