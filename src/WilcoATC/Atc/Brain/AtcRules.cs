using System.IO;
using System.Reflection;
using System.Text.Json;

namespace WilcoATC.Atc.Brain;

/// <summary>
/// Texte d'une transmission. L'anglais (<c>en</c>) est la référence : il existe toujours et
/// sert de repli. Les autres langues vivent dans <c>i18n</c>, indexées par code ISO — ainsi
/// une langue s'ajoute en éditant le JSON, sans toucher au C#.
/// </summary>
public sealed class LocalizedText
{
    public string En { get; set; } = "";

    /// <summary>Traductions par code ISO (« fr », « de »…). Absente -> repli sur l'anglais.</summary>
    public Dictionary<string, string> I18n { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string For(AtcLanguage lang)
        => I18n.TryGetValue(lang.Code(), out var s) && !string.IsNullOrWhiteSpace(s) ? s : En;
}

/// <summary>Une règle : quelles conditions autorisent une intention, et la réponse d'accord.</summary>
public sealed class AtcRule
{
    public string Intent { get; set; } = "";

    /// <summary>
    /// Règles de vol auxquelles cette règle s'applique : « VFR », « IFR », ou vide pour
    /// les deux. C'est ce qui permet à une même intention (demander la clairance) d'avoir
    /// deux réponses radicalement différentes selon qu'on vole à vue ou aux instruments.
    /// </summary>
    public string? FlightRules { get; set; }

    // NOTE : plus d'« allowedPhases ». Un refus fondé sur la phase de vol (elle-même
    // devinée à partir de SimVars) bloquait des demandes valables sans recours possible.
    public List<string> AllowedControllers { get; set; } = new();
    public bool RequireOnGround { get; set; }
    public string? AdvanceToPhase { get; set; }
    public string Approved { get; set; } = "";
    // Variante de repli quand aucun SID n'est disponible (ex. clairance « as filed »).
    public string? ApprovedNoSid { get; set; }

    /// <summary>Traductions de <see cref="Approved"/>, par code ISO. Absente -> anglais.</summary>
    public Dictionary<string, string> ApprovedI18n { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Traductions de <see cref="ApprovedNoSid"/>, par code ISO.</summary>
    public Dictionary<string, string> ApprovedNoSidI18n { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string ApprovedFor(AtcLanguage lang)
        => ApprovedI18n.TryGetValue(lang.Code(), out var s) && !string.IsNullOrWhiteSpace(s) ? s : Approved;

    public string? ApprovedNoSidFor(AtcLanguage lang)
        => ApprovedNoSidI18n.TryGetValue(lang.Code(), out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : ApprovedNoSid;

    /// <summary>
    /// Variante employée quand l'appareil ARRIVE — il s'est posé et rejoint son poste — au
    /// lieu de partir.
    ///
    /// Une même demande (« request taxi ») veut dire deux choses opposées selon le moment :
    /// avant le vol, rejoindre le point d'attente ; après l'atterrissage, rejoindre le
    /// parking. Sans cette variante, un pilote qui vient de dégager la piste s'entendait
    /// renvoyer au point d'attente pour redécoller.
    ///
    /// Ce n'est PAS le retour des « allowedPhases » : la phase ne refuse toujours rien, elle
    /// choisit seulement laquelle des deux formulations correspond à la situation.
    /// </summary>
    public string? ApprovedArriving { get; set; }

    /// <summary>Traductions de <see cref="ApprovedArriving"/>, par code ISO.</summary>
    public Dictionary<string, string> ApprovedArrivingI18n { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Variante employée quand le pilote demande la MISE EN ROUTE en même temps que sa
    /// clairance — « request startup, destination… », la tournure standard en Europe.
    ///
    /// Le contrôleur accorde alors les deux d'un coup : « startup approved » puis la
    /// clairance. Répondre la seule clairance laisse le pilote sans la moitié de ce qu'il a
    /// demandé, et l'oblige à redemander ce qui vient de lui être implicitement accordé.
    /// </summary>
    public string? ApprovedStartup { get; set; }

    /// <summary>Traductions de <see cref="ApprovedStartup"/>, par code ISO.</summary>
    public Dictionary<string, string> ApprovedStartupI18n { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mise en route accordée alors qu'AUCUN SID n'est connu. <see cref="ApprovedStartup"/>
    /// cite la procédure de départ (« CIV 2 Delta departure, level 70 ») : sans SID chargé,
    /// cette phrase n'aurait plus de sujet. On retombe alors sur la clairance « as filed ».
    /// </summary>
    public string? ApprovedStartupNoSid { get; set; }

    /// <summary>Traductions de <see cref="ApprovedStartupNoSid"/>, par code ISO.</summary>
    public Dictionary<string, string> ApprovedStartupNoSidI18n { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Accusé de collationnement qui SUIT une clairance de départ : le contrôleur y ajoute le
    /// passage au Sol (« report ready for pushback on Brussels Ground on 110.100 »), comme le
    /// fait une délivrance européenne — c'est elle qui rend la main, pas le pilote.
    /// </summary>
    public string? ApprovedAfterClearance { get; set; }

    /// <summary>Traductions de <see cref="ApprovedAfterClearance"/>, par code ISO.</summary>
    public Dictionary<string, string> ApprovedAfterClearanceI18n { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ApprovedStartupFor(AtcLanguage lang)
        => ApprovedStartupI18n.TryGetValue(lang.Code(), out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : ApprovedStartup;

    public string? ApprovedStartupNoSidFor(AtcLanguage lang)
        => ApprovedStartupNoSidI18n.TryGetValue(lang.Code(), out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : ApprovedStartupNoSid;

    public string? ApprovedAfterClearanceFor(AtcLanguage lang)
        => ApprovedAfterClearanceI18n.TryGetValue(lang.Code(), out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : ApprovedAfterClearance;

    public string? ApprovedArrivingFor(AtcLanguage lang)
        => ApprovedArrivingI18n.TryGetValue(lang.Code(), out var s) && !string.IsNullOrWhiteSpace(s)
            ? s
            : ApprovedArriving;
}

/// <summary>Table de règles + phrases de refus + événements proactifs, chargée depuis JSON.</summary>
public sealed class AtcRuleSet
{
    /// <summary>Version du schéma embarqué : si le fichier disque est plus ancien, il est régénéré.</summary>
    public int Version { get; set; }
    public List<AtcRule> Rules { get; set; } = new();
    public Dictionary<string, string> Denials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Refus traduits : code ISO -> (motif -> phrase). Absent -> <see cref="Denials"/>.</summary>
    public Dictionary<string, Dictionary<string, string>> DenialsI18n { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Phrase de refus pour un motif, dans une langue. Null si le motif est inconnu.</summary>
    public string? Denial(string reasonKey, AtcLanguage lang)
    {
        if (DenialsI18n.TryGetValue(lang.Code(), out var table)
            && table.TryGetValue(reasonKey, out var localized)
            && !string.IsNullOrWhiteSpace(localized))
            return localized;

        return Denials.TryGetValue(reasonKey, out var en) ? en : null;
    }

    /// <summary>Transmissions initiées par l'ATC (proactif) : clé -> texte.</summary>
    public Dictionary<string, LocalizedText> Events { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Charge la table : fichier éditable dans %LOCALAPPDATA%\WilcoATC\atc-rules.json
    /// (créé au premier lancement depuis la ressource embarquée), sinon la ressource.
    /// </summary>
    public static AtcRuleSet Load()
    {
        var embedded = FromEmbedded();
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WilcoATC");
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
