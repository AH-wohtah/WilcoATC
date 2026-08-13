using WilcoATC.Atc;
using WilcoATC.Common;

namespace WilcoATC.Audio;

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
        => For(identity, language, ControllerType.Unknown);

    /// <summary>
    /// Voix attribuée à une identité, avec un CARACTÈRE propre au type de contrôleur.
    ///
    /// Deux effets distincts, à ne pas confondre :
    ///  • le type entre dans le hachage -> le Sol et la Tour d'un MÊME aéroport ne peuvent
    ///    plus tomber sur la même voix, alors que le hachage du seul nom de station le
    ///    permettait par collision ;
    ///  • le type impose un biais de DÉBIT -> un Centre en-route parle posément, une Tour
    ///    chargée débite. C'est ce qui fait qu'on reconnaît son interlocuteur à l'oreille.
    /// </summary>
    public TtsVoice For(string? identity, AtcLanguage language, ControllerType controller)
    {
        string wanted = language.Code();
        var all = _voices.List().Select(v => v.Name).ToList();
        if (all.Count == 0) return TtsVoice.Default;   // rien d'installé -> repli du moteur

        // 1) Voix de la bonne langue. 2) À défaut, l'ANGLAIS : un contrôleur français sans
        //    voix française parlera anglais, ce qui est correct — alors que lui faire lire
        //    du français par un modèle anglais donnerait une bouillie inécoutable.
        //    3) Puis les langues inconnues (modèles exotiques), 4) n'importe quoi en dernier.
        var pool = all.Where(n => LanguageOf(n) == wanted).ToList();
        if (pool.Count == 0 && wanted != "en") pool = all.Where(n => LanguageOf(n) == "en").ToList();
        if (pool.Count == 0) pool = all.Where(n => LanguageOf(n) is null).ToList();
        if (pool.Count == 0) pool = all;

        // Le type est préfixé à l'identité : même station + type différent = hachage différent.
        string key = controller == ControllerType.Unknown
            ? identity ?? ""
            : controller + "|" + (identity ?? "");
        uint h = key.Length == 0 ? 0u : Fnv1a(key);

        string name = pool[(int)(h % (uint)pool.Count)];
        int speaker = (int)((h >> 8) % 64);              // borné par le moteur selon le modèle
        float speed = 0.94f + (h >> 16) % 13 * 0.01f;    // 0.94 .. 1.06

        return new TtsVoice(name, speaker, speed * SpeedBias(controller));
    }

    /// <summary>
    /// Débit caractéristique d'une position de contrôle. Volontairement DISCRET (±6 % au
    /// plus) : au-delà, la voix s'entend comme accélérée au lieu de s'entendre comme une
    /// personne différente.
    /// </summary>
    private static float SpeedBias(ControllerType c) => c switch
    {
        ControllerType.Ground or ControllerType.Clearance => 0.96f, // au sol, on prend son temps
        ControllerType.Tower => 1.06f,                              // tour chargée, ça débite
        ControllerType.Departure or ControllerType.Approach => 1.02f,
        ControllerType.Center => 0.98f,                             // en-route, posé et régulier
        _ => 1f,
    };

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
