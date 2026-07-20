using System.IO;

namespace FreqWatch.Audio;

/// <summary>Chemins d'un modèle Whisper (encodeur + décodeur + tokens) installé.</summary>
public sealed record WhisperModel(string EncoderPath, string DecoderPath, string TokensPath, string Name);

/// <summary>
/// Gère le dossier des modèles de reconnaissance vocale (<c>%LOCALAPPDATA%\FreqWatch\asr</c>
/// par défaut) : découverte du modèle Whisper installé (encodeur/décodeur/tokens).
///
/// Modèle par défaut : <c>sherpa-onnx-whisper-tiny</c> (MULTILINGUE, gère FR + EN), offline,
/// distribué en <c>.tar.bz2</c> par le projet sherpa-onnx — même format que les voix.
/// </summary>
public sealed class WhisperModelRepository
{
    public const string DefaultModelName = "sherpa-onnx-whisper-tiny";
    public const string DefaultModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-whisper-tiny.tar.bz2";

    public string ModelsDir { get; }

    public WhisperModelRepository(string? modelsDir = null)
    {
        ModelsDir = string.IsNullOrWhiteSpace(modelsDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FreqWatch", "asr")
            : modelsDir!;
    }

    public bool IsInstalled => Resolve() is not null;

    /// <summary>
    /// Résout le modèle installé (recherche récursive : l'archive extrait dans un sous-dossier).
    /// Préfère les variantes int8 (plus légères/rapides), sinon fp32. L'encodeur et le
    /// décodeur sont appariés sur la même variante.
    /// </summary>
    public WhisperModel? Resolve()
    {
        if (!Directory.Exists(ModelsDir)) return null;

        // int8 en priorité (les deux fichiers doivent exister pour être appariés).
        string? encoder = Find("*-encoder.int8.onnx");
        string? decoder = Find("*-decoder.int8.onnx");
        if (encoder is null || decoder is null)
        {
            encoder = Find("*-encoder.onnx");
            decoder = Find("*-decoder.onnx");
        }

        string? tokens = Find("*-tokens.txt") ?? Find("tokens.txt");
        if (encoder is null || decoder is null || tokens is null) return null;

        string name = Path.GetFileName(Path.GetDirectoryName(encoder)!);
        return new WhisperModel(encoder, decoder, tokens, name);
    }

    private string? Find(string pattern)
    {
        try { return Directory.EnumerateFiles(ModelsDir, pattern, SearchOption.AllDirectories).FirstOrDefault(); }
        catch { return null; }
    }
}
