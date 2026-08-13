using System.IO;

namespace WilcoATC.Audio;

/// <summary>Famille de modèle de reconnaissance vocale : le moteur se configure différemment.</summary>
public enum SpeechEngine
{
    /// <summary>Whisper (encodeur + décodeur, décodage autorégressif).</summary>
    Whisper,

    /// <summary>Transducteur NeMo type Parakeet (encodeur + décodeur + joiner).</summary>
    NemoTransducer,
}

/// <summary>
/// Un modèle de reconnaissance installé. <see cref="JoinerPath"/> n'est renseigné que
/// pour un transducteur.
/// </summary>
public sealed record SpeechModel(
    SpeechEngine Engine,
    string EncoderPath,
    string DecoderPath,
    string TokensPath,
    string Name,
    string? JoinerPath = null);

/// <summary>
/// Gère le dossier des modèles de reconnaissance vocale (<c>%LOCALAPPDATA%\WilcoATC\asr</c>
/// par défaut) et choisit le meilleur modèle installé.
///
/// MODÈLE PAR DÉFAUT : <b>Parakeet TDT 0.6B v2</b> (NVIDIA NeMo, int8, ~460 Mo), gratuit et
/// hors-ligne comme le reste. ANGLAIS UNIQUEMENT, et c'est voulu : la compréhension
/// multilingue est coupée pour l'instant, et sur de l'anglais la v2 bat la v3 multilingue.
/// Mesuré sur un corpus de phraséologie ATC synthétisée avec trois voix différentes :
///
///     parakeet-tdt-0.6b-v2   WER  8,8 %   (0,15 s / phrase)
///     whisper-base.en        WER 14,4 %   (0,18 s / phrase)
///     whisper-tiny           WER 14,7 %   (0,13 s / phrase)
///
/// Soit ~40 % d'erreurs en moins que Whisper, sans coût en temps de réponse. Les modèles
/// Whisper restent pris en charge : une installation existante continue de fonctionner et
/// c'est le meilleur modèle PRÉSENT qui est retenu (voir <see cref="Resolve"/>). La v3
/// multilingue, si elle est déjà installée, fonctionne aussi — elle n'est simplement plus
/// proposée au téléchargement.
/// </summary>
public sealed class SpeechModelRepository
{
    public const string DefaultModelName = "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8";
    public const string DefaultModelUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2";

    /// <summary>Vrai si le modèle installé comprend autre chose que l'anglais.</summary>
    public bool IsMultilingual => Resolve() is { } m && IsMultilingualModel(m.Name);

    /// <summary>
    /// Un modèle est multilingue s'il n'est PAS marqué anglais. Les modèles anglais se
    /// signalent : « .en » chez Whisper, « v2 » pour Parakeet (la v3 est la multilingue).
    /// </summary>
    private static bool IsMultilingualModel(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains(".en") || n.EndsWith("-en")) return false;
        if (n.Contains("parakeet")) return n.Contains("v3");
        return true;   // Whisper sans suffixe « .en » est multilingue
    }

    public string ModelsDir { get; }

    public SpeechModelRepository(string? modelsDir = null)
    {
        ModelsDir = string.IsNullOrWhiteSpace(modelsDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WilcoATC", "asr")
            : modelsDir!;
    }

    public bool IsInstalled => Resolve() is not null;

    /// <summary>
    /// Meilleur modèle installé, ou null. Plusieurs peuvent cohabiter (l'utilisateur a pu
    /// télécharger Whisper avant Parakeet) : on les classe au lieu de prendre le premier venu.
    /// </summary>
    public SpeechModel? Resolve()
    {
        if (!Directory.Exists(ModelsDir)) return null;

        SpeechModel? best = null;
        int bestScore = int.MinValue;

        foreach (var dir in Candidates())
        {
            var found = ReadTransducer(dir) ?? ReadWhisper(dir);
            if (found is null) continue;

            int score = Score(found);
            if (score <= bestScore) continue;
            bestScore = score;
            best = found;
        }
        return best;
    }

    /// <summary>Transducteur : encodeur + décodeur + joiner. Le joiner est la signature.</summary>
    private static SpeechModel? ReadTransducer(string dir)
    {
        foreach (bool int8 in new[] { true, false })
        {
            string sfx = int8 ? ".int8.onnx" : ".onnx";
            string? joiner = Pick(dir, "*joiner*" + sfx, int8);
            string? encoder = Pick(dir, "*encoder*" + sfx, int8);
            string? decoder = Pick(dir, "*decoder*" + sfx, int8);
            string? tokens = FindIn(dir, "*tokens.txt") ?? FindIn(dir, "tokens.txt");
            if (joiner is null || encoder is null || decoder is null || tokens is null) continue;

            return new SpeechModel(SpeechEngine.NemoTransducer, encoder, decoder, tokens,
                                   Path.GetFileName(dir), joiner);
        }
        return null;
    }

    /// <summary>Whisper : encodeur + décodeur, sans joiner.</summary>
    private static SpeechModel? ReadWhisper(string dir)
    {
        foreach (bool int8 in new[] { true, false })
        {
            string sfx = int8 ? ".int8.onnx" : ".onnx";
            string? encoder = Pick(dir, "*encoder*" + sfx, int8);
            string? decoder = Pick(dir, "*decoder*" + sfx, int8);
            string? tokens = FindIn(dir, "*tokens.txt") ?? FindIn(dir, "tokens.txt");
            if (encoder is null || decoder is null || tokens is null) continue;

            return new SpeechModel(SpeechEngine.Whisper, encoder, decoder, tokens, Path.GetFileName(dir));
        }
        return null;
    }

    /// <summary>
    /// Priorité : Parakeet d'abord (le plus précis), puis les Whisper par taille, l'anglais
    /// avant le multilingue à taille égale — tant que l'ATC ne comprend que l'anglais, un
    /// modèle spécialisé y est meilleur qu'un modèle qui couvre vingt-cinq langues.
    /// </summary>
    private static int Score(SpeechModel m)
    {
        string n = m.Name.ToLowerInvariant();

        if (m.Engine == SpeechEngine.NemoTransducer)
            return 1000 + (n.Contains("0.6b") ? 10 : 0) + (IsMultilingualModel(n) ? 0 : 5);

        int size = n.Contains("medium") ? 4 : n.Contains("small") ? 3
                 : n.Contains("base") ? 2 : n.Contains("tiny") ? 1 : 0;
        if (n.Contains("distil")) size = Math.Max(1, size - 1);
        int english = n.Contains(".en") || n.EndsWith("-en") ? 1 : 0;

        return size * 10 + english;
    }

    private IEnumerable<string> Candidates()
    {
        yield return ModelsDir;
        string[] subs;
        try { subs = Directory.GetDirectories(ModelsDir, "*", SearchOption.AllDirectories); }
        catch { yield break; }
        foreach (var s in subs) yield return s;
    }

    // « *encoder*.onnx » capturerait aussi « encoder.int8.onnx » : on filtre explicitement.
    private static string? Pick(string dir, string pattern, bool int8)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
                            .FirstOrDefault(f => f.Contains(".int8.") == int8);
        }
        catch { return null; }
    }

    private static string? FindIn(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly).FirstOrDefault(); }
        catch { return null; }
    }
}
