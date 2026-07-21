namespace FreqWatch.Audio;

/// <summary>Une voix téléchargeable depuis le dépôt de modèles sherpa-onnx.</summary>
/// <param name="MultiSpeaker">
/// Modèle MULTI-LOCUTEURS : un seul téléchargement fournit des dizaines/centaines de voix
/// différentes. Comme <see cref="VoicePicker"/> fait varier le locuteur, c'est le meilleur
/// rapport « nombre de voix / Mo » pour que chaque contrôleur et chaque équipage sonne
/// différemment.
/// </param>
public sealed record CatalogVoice(string Name, string Language, string Quality, bool MultiSpeaker = false)
{
    public string Url =>
        $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{Name}.tar.bz2";

    public string Display =>
        $"{Language} · {Name} ({Quality}{(MultiSpeaker ? " · multi-voix" : "")})";

    public override string ToString() => Display;
}

/// <summary>
/// Catalogue de voix Piper (sherpa-onnx) proposées au téléchargement dans les réglages.
/// Toutes les URL ont été vérifiées comme disponibles sur le dépôt de modèles.
///
/// QUALITÉ : seuls les paliers <b>medium</b> et <b>high</b> sont proposés. Le palier
/// « low » a été retiré volontairement — ce sont des modèles réduits échantillonnés à
/// 16 kHz (contre 22,05 kHz), à la voix nettement plus métallique/robotique.
///
/// LANGUE : anglais uniquement pour l'instant (US + GB).
///
/// On peut toujours déposer manuellement n'importe quel modèle sherpa-onnx dans le dossier
/// des voix : il sera détecté automatiquement.
/// </summary>
public static class VoiceCatalog
{
    public static readonly IReadOnlyList<CatalogVoice> Voices = new[]
    {
        // ---- Multi-locuteurs (recommandés : beaucoup de voix d'un coup) ----
        new CatalogVoice("vits-piper-en_US-libritts_r-medium", "English (US)", "medium", MultiSpeaker: true),
        new CatalogVoice("vits-piper-en_US-libritts-high",     "English (US)", "high",   MultiSpeaker: true),
        new CatalogVoice("vits-piper-en_GB-vctk-medium",       "English (GB)", "medium", MultiSpeaker: true),
        new CatalogVoice("vits-piper-en_US-arctic-medium",     "English (US)", "medium", MultiSpeaker: true),
        new CatalogVoice("vits-piper-en_US-l2arctic-medium",   "English (US, accents)", "medium", MultiSpeaker: true),
        new CatalogVoice("vits-piper-en_GB-aru-medium",        "English (GB)", "medium", MultiSpeaker: true),
        new CatalogVoice("vits-piper-en_GB-semaine-medium",    "English (GB)", "medium", MultiSpeaker: true),

        // ---- Anglais américain ----
        new CatalogVoice("vits-piper-en_US-ryan-high",         "English (US)", "high"),
        new CatalogVoice("vits-piper-en_US-ryan-medium",       "English (US)", "medium"),
        new CatalogVoice("vits-piper-en_US-lessac-high",       "English (US)", "high"),
        new CatalogVoice("vits-piper-en_US-lessac-medium",     "English (US)", "medium"),
        new CatalogVoice("vits-piper-en_US-amy-medium",        "English (US)", "medium"),
        new CatalogVoice("vits-piper-en_US-joe-medium",        "English (US)", "medium"),
        new CatalogVoice("vits-piper-en_US-john-medium",       "English (US)", "medium"),
        new CatalogVoice("vits-piper-en_US-norman-medium",     "English (US)", "medium"),
        new CatalogVoice("vits-piper-en_US-bryce-medium",      "English (US)", "medium"),
        new CatalogVoice("vits-piper-en_US-kusal-medium",      "English (US)", "medium"),
        new CatalogVoice("vits-piper-en_US-hfc_male-medium",   "English (US)", "medium"),
        new CatalogVoice("vits-piper-en_US-hfc_female-medium", "English (US)", "medium"),

        // ---- Anglais britannique ----
        new CatalogVoice("vits-piper-en_GB-cori-high",         "English (GB)", "high"),
        new CatalogVoice("vits-piper-en_GB-cori-medium",       "English (GB)", "medium"),
        new CatalogVoice("vits-piper-en_GB-alan-medium",       "English (GB)", "medium"),
        new CatalogVoice("vits-piper-en_GB-alba-medium",       "English (GB)", "medium"),
        new CatalogVoice("vits-piper-en_GB-jenny_dioco-medium","English (GB)", "medium"),
        new CatalogVoice("vits-piper-en_GB-northern_english_male-medium", "English (GB)", "medium"),
    };
}
