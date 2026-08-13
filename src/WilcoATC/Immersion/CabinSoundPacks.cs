using System.IO;
using System.Text.Json;
using WilcoATC.Audio;

namespace WilcoATC.Immersion;

/// <summary>Un pack de sons de cabine : un nom + la table « événement -> fichier audio ».</summary>
public sealed record CabinSoundPack(string Name, string Directory, IReadOnlyDictionary<string, string> Events)
{
    /// <summary>Chemin du fichier pour un événement, ou null si le pack ne le fournit pas.</summary>
    public string? FileFor(string eventKey)
        => Events.TryGetValue(eventKey, out var path) && File.Exists(path) ? path : null;

    /// <summary>Affiché dans les réglages : on montre combien d'événements ont été reconnus.</summary>
    public override string ToString()
        => $"{Name}  ({Events.Count}/{CabinSoundPackRepository.KnownEvents.Length})";
}

/// <summary>
/// Découverte des packs de sons de cabine (façon Fenix) dans
/// <c>%LOCALAPPDATA%\WilcoATC\cabin</c> : un SOUS-DOSSIER = un pack (les fichiers déposés
/// directement à la racine forment aussi un pack).
///
/// La reconnaissance des fichiers est VOLONTAIREMENT TOLÉRANTE : il suffit que le nom
/// CONTIENNE un mot-clé de l'événement, dans n'importe quelle casse, avec ou sans préfixe.
/// « PA_Boarding.ogg », « 01 - welcome.ogg » ou « boarding.wav » marchent tous.
/// Un manifest <c>pack.json</c> optionnel reste prioritaire :
/// <c>{ "name": "Mon pack", "events": { "boarding": "pa_boarding.ogg" } }</c>
/// </summary>
public sealed class CabinSoundPackRepository
{
    /// <summary>Événements reconnus (déclenchés par la phase de vol).</summary>
    public static readonly string[] KnownEvents =
    {
        "boarding", "safety", "takeoff", "cruise", "descent", "landing", "deboarding",
    };

    /// <summary>
    /// Mots-clés par événement. L'ORDRE COMPTE : « deboarding » est testé avant « boarding »,
    /// sinon « deboarding.ogg » (qui contient « boarding ») serait mal classé.
    /// </summary>
    private static readonly (string Event, string[] Keywords)[] Aliases =
    {
        ("deboarding", new[] { "deboard", "disembark", "debarqu", "débarqu", "farewell", "goodbye" }),
        ("boarding",   new[] { "boarding", "welcome", "embarqu", "bienvenue" }),
        ("safety",     new[] { "safety", "securit", "sécurit", "demo", "briefing", "consigne" }),
        ("takeoff",    new[] { "takeoff", "take-off", "take_off", "departure", "decoll", "décoll" }),
        ("cruise",     new[] { "cruise", "croisi", "climb", "montee", "montée" }),
        ("descent",    new[] { "descent", "descend", "descente", "approach", "approche" }),
        ("landing",    new[] { "landing", "atterri", "arrival", "arrivee", "arrivée", "touchdown" }),
    };

    public string PacksDir { get; }

    public CabinSoundPackRepository(string? packsDir = null)
    {
        PacksDir = string.IsNullOrWhiteSpace(packsDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                           "WilcoATC", "cabin")
            : packsDir!;
    }

    /// <summary>Packs installés : tout dossier contenant au moins un fichier audio lisible.</summary>
    public IReadOnlyList<CabinSoundPack> List()
    {
        var packs = new List<CabinSoundPack>();
        try
        {
            if (!Directory.Exists(PacksDir)) return packs;

            // Fichiers déposés directement dans le dossier racine.
            var root = TryLoad(PacksDir);
            if (root is not null) packs.Add(root);

            foreach (var dir in Directory.GetDirectories(PacksDir))
            {
                var pack = TryLoad(dir);
                if (pack is not null) packs.Add(pack);
            }
        }
        catch { /* dossier illisible -> aucun pack */ }
        return packs;
    }

    /// <summary>Pack demandé par nom, sinon le premier installé, sinon null.</summary>
    public CabinSoundPack? Resolve(string? name)
    {
        var all = List();
        if (all.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var hit = all.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return all[0];
    }

    /// <summary>Événement correspondant à un nom de fichier, ou null.</summary>
    public static string? EventForFileName(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        foreach (var (evt, keywords) in Aliases)
            if (keywords.Any(k => stem.Contains(k, StringComparison.Ordinal)))
                return evt;
        return null;
    }

    private static CabinSoundPack? TryLoad(string dir)
    {
        var events = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        bool anyAudio = false;

        // 1) Convention tolérante : le nom du fichier CONTIENT un mot-clé d'événement.
        foreach (var file in SafeFiles(dir))
        {
            if (!CabinAudioPlayer.SupportedExtensions
                    .Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;

            anyAudio = true;
            string? evt = EventForFileName(file);
            if (evt is not null && !events.ContainsKey(evt)) events[evt] = file;
        }

        // 2) Manifest optionnel (prioritaire sur la convention).
        string manifest = Path.Combine(dir, "pack.json");
        if (File.Exists(manifest))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                var root = doc.RootElement;
                if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(n.GetString()))
                    name = n.GetString()!;

                if (root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in evs.EnumerateObject())
                    {
                        string? rel = p.Value.GetString();
                        if (string.IsNullOrWhiteSpace(rel)) continue;
                        string full = Path.Combine(dir, rel!);
                        if (File.Exists(full)) { events[p.Name] = full; anyAudio = true; }
                    }
                }
            }
            catch { /* manifest cassé -> on garde la convention */ }
        }

        // On expose le pack DÈS QU'IL CONTIENT DE L'AUDIO, même si aucun nom n'a été reconnu :
        // l'utilisateur le voit dans la liste (avec « 0/7 ») au lieu d'un dossier ignoré en silence.
        return anyAudio ? new CabinSoundPack(name, dir, events) : null;
    }

    private static IEnumerable<string> SafeFiles(string dir)
    {
        try { return Directory.GetFiles(dir); }
        catch { return Array.Empty<string>(); }
    }
}
