using System.IO;

namespace FreqWatch.Audio;

/// <summary>
/// Gère le dossier des voix (<c>%LOCALAPPDATA%\FreqWatch\voices</c> par défaut) :
/// découverte des voix installées et résolution de la voix sélectionnée.
/// </summary>
public sealed class VoiceRepository
{
    /// <summary>Voix par défaut (téléchargée au premier lancement).</summary>
    public const string DefaultVoiceName = "vits-piper-en_US-ryan-medium";

    public const string DefaultVoiceUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/vits-piper-en_US-ryan-medium.tar.bz2";

    public string VoicesDir { get; }

    public VoiceRepository(string? voicesDir = null)
    {
        VoicesDir = string.IsNullOrWhiteSpace(voicesDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FreqWatch", "voices")
            : voicesDir!;
    }

    /// <summary>Voix installées (dossiers valides sous <see cref="VoicesDir"/>).</summary>
    public IReadOnlyList<VoiceModel> List()
    {
        var result = new List<VoiceModel>();
        if (!Directory.Exists(VoicesDir)) return result;

        foreach (var dir in Directory.GetDirectories(VoicesDir))
        {
            var v = TryLoad(dir);
            if (v is not null) result.Add(v);
        }
        return result;
    }

    public bool HasAnyVoice() => List().Count > 0;

    /// <summary>Résout la voix demandée par nom ; sinon la voix par défaut ; sinon la première.</summary>
    public VoiceModel? Resolve(string? name)
    {
        var all = List();
        if (all.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            var match = all.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return all.FirstOrDefault(v => v.Name.Equals(DefaultVoiceName, StringComparison.OrdinalIgnoreCase))
               ?? all[0];
    }

    // Un dossier est une voix valide s'il contient un .onnx (hors *.onnx.json),
    // tokens.txt et espeak-ng-data/.
    private static VoiceModel? TryLoad(string dir)
    {
        string? onnx = Directory.GetFiles(dir, "*.onnx")
            .FirstOrDefault(f => !f.EndsWith(".onnx.json", StringComparison.OrdinalIgnoreCase));
        string tokens = Path.Combine(dir, "tokens.txt");
        string dataDir = Path.Combine(dir, "espeak-ng-data");

        if (onnx is null || !File.Exists(tokens) || !Directory.Exists(dataDir)) return null;

        return new VoiceModel
        {
            Name = Path.GetFileName(dir),
            OnnxPath = onnx,
            TokensPath = tokens,
            DataDir = dataDir,
        };
    }
}
