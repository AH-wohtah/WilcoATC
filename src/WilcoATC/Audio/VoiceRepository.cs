using System.IO;

namespace WilcoATC.Audio;

/// <summary>
/// Gère le dossier des voix (<c>%LOCALAPPDATA%\WilcoATC\voices</c> par défaut) :
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
                "WilcoATC", "voices")
            : voicesDir!;
    }

    private readonly object _gate = new();
    private IReadOnlyList<VoiceModel>? _cache;
    private DateTime _cacheStamp;

    /// <summary>
    /// Voix installées (dossiers valides sous <see cref="VoicesDir"/>).
    ///
    /// RÉSULTAT MIS EN CACHE : un inventaire complet coûte trois accès disque PAR VOIX
    /// (~110 appels système avec 36 voix installées), et cette méthode est appelée plusieurs
    /// fois par transmission — choix de la voix, résolution du modèle, test de disponibilité
    /// d'une langue. Le cache est invalidé par la DATE du dossier, qui change dès qu'une voix
    /// est ajoutée ou retirée : déposer un modèle à la main reste donc détecté, sans
    /// redémarrage et sans re-scanner à chaque phrase.
    /// </summary>
    public IReadOnlyList<VoiceModel> List()
    {
        if (!Directory.Exists(VoicesDir)) return Array.Empty<VoiceModel>();

        DateTime stamp = Directory.GetLastWriteTimeUtc(VoicesDir);

        lock (_gate)
        {
            if (_cache is not null && stamp == _cacheStamp) return _cache;

            var result = new List<VoiceModel>();
            foreach (var dir in Directory.GetDirectories(VoicesDir))
            {
                var v = TryLoad(dir);
                if (v is not null) result.Add(v);
            }

            _cache = result;
            _cacheStamp = stamp;
            return result;
        }
    }

    /// <summary>
    /// Force le prochain <see cref="List"/> à relire le disque. Utile juste après une
    /// installation : l'extraction peut remplir un dossier déjà créé, ce que la date du
    /// dossier parent ne reflète alors pas.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate) _cache = null;
    }

    public bool HasAnyVoice() => List().Count > 0;

    /// <summary>
    /// Une voix est-elle installée pour ce code langue (« fr », « de »…) ?
    ///
    /// C'est cette réponse qui autorise le contrôleur à PARLER cette langue : sans modèle
    /// vocal correspondant, il reste en anglais. Faire lire du français par un modèle
    /// anglais ne donne pas un accent, ça donne une bouillie — mieux vaut la phraséologie
    /// OACI, qui est de toute façon valable partout.
    /// </summary>
    public bool HasVoiceFor(string languageCode)
        => List().Any(v => string.Equals(VoicePicker.LanguageOf(v.Name), languageCode,
                                         StringComparison.OrdinalIgnoreCase));

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
