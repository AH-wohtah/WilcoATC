namespace FreqWatch.Audio;

/// <summary>Une voix téléchargeable depuis le dépôt de modèles sherpa-onnx.</summary>
public sealed record CatalogVoice(string Name, string Language, string Quality)
{
    public string Url =>
        $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{Name}.tar.bz2";

    public string Display => $"{Language} · {Name} ({Quality})";

    public override string ToString() => Display;
}

/// <summary>
/// Catalogue de voix Piper (sherpa-onnx) proposées au téléchargement dans les réglages,
/// dont plusieurs voix <b>françaises</b>. On peut aussi déposer manuellement n'importe
/// quel modèle sherpa-onnx dans le dossier des voix.
/// </summary>
public static class VoiceCatalog
{
    public static readonly IReadOnlyList<CatalogVoice> Voices = new[]
    {
        // Anglais (défaut)
        new CatalogVoice("vits-piper-en_US-ryan-medium", "English (US)", "medium"),
        // Français
        new CatalogVoice("vits-piper-fr_FR-siwis-medium", "Français", "medium"),
        new CatalogVoice("vits-piper-fr_FR-tom-medium",   "Français", "medium"),
        new CatalogVoice("vits-piper-fr_FR-upmc-medium",  "Français", "medium"),
        new CatalogVoice("vits-piper-fr_FR-gilles-low",   "Français", "low"),
        new CatalogVoice("vits-piper-fr_FR-siwis-low",    "Français", "low"),
    };
}
