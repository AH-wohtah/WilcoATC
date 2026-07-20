using System.IO;
using System.Reflection;
using System.Text.Json;

namespace FreqWatch.Atc.Brain;

/// <summary>Texte bilingue (anglais / français).</summary>
public sealed class LocalizedText
{
    public string En { get; set; } = "";
    public string Fr { get; set; } = "";
}

/// <summary>Une règle : quelles conditions autorisent une intention, et la réponse d'accord.</summary>
public sealed class AtcRule
{
    public string Intent { get; set; } = "";
    public List<string> AllowedPhases { get; set; } = new();
    public List<string> AllowedControllers { get; set; } = new();
    public bool RequireOnGround { get; set; }
    public string? AdvanceToPhase { get; set; }
    public string Approved { get; set; } = "";
    public string? ApprovedFr { get; set; }
    // Variante de repli quand aucun SID n'est disponible (ex. clairance « as filed »).
    public string? ApprovedNoSid { get; set; }
    public string? ApprovedNoSidFr { get; set; }
}

/// <summary>Table de règles + phrases de refus + événements proactifs, chargée depuis JSON.</summary>
public sealed class AtcRuleSet
{
    /// <summary>Version du schéma embarqué : si le fichier disque est plus ancien, il est régénéré.</summary>
    public int Version { get; set; }
    public List<AtcRule> Rules { get; set; } = new();
    public Dictionary<string, string> Denials { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> DenialsFr { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Transmissions initiées par l'ATC (proactif) : clé -> texte bilingue.</summary>
    public Dictionary<string, LocalizedText> Events { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Charge la table : fichier éditable dans %LOCALAPPDATA%\FreqWatch\atc-rules.json
    /// (créé au premier lancement depuis la ressource embarquée), sinon la ressource.
    /// </summary>
    public static AtcRuleSet Load()
    {
        var embedded = FromEmbedded();
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FreqWatch");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "atc-rules.json");

            // Fichier absent OU plus ancien que la ressource embarquée -> (re)génération.
            if (!File.Exists(path))
            {
                File.WriteAllText(path, ReadEmbedded());
                return embedded;
            }

            var disk = JsonSerializer.Deserialize<AtcRuleSet>(File.ReadAllText(path), Options);
            if (disk is null || disk.Version < embedded.Version)
            {
                File.WriteAllText(path, ReadEmbedded()); // met à jour la copie éditable
                return embedded;
            }
            return disk;
        }
        catch
        {
            return embedded;
        }
    }

    private static AtcRuleSet FromEmbedded()
        => JsonSerializer.Deserialize<AtcRuleSet>(ReadEmbedded(), Options) ?? new AtcRuleSet();

    private static string ReadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("atc-rules.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return "{}";
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
