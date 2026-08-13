using System.IO;
using WilcoATC.Diagnostics;

namespace WilcoATC.Audio;

/// <summary>Catégorie d'échantillon radio. Le nom du fichier suffit à le classer.</summary>
public enum RadioSampleKind
{
    /// <summary>Alternat enfoncé : le déclic mécanique au DÉBUT de la transmission.</summary>
    KeyUp,

    /// <summary>Respiration du pilote, juste avant qu'il ne parle.</summary>
    Breath,

    /// <summary>Queue de squelch : la bouffée à la FIN, quand la porteuse tombe.</summary>
    Tail,

    /// <summary>Fond sonore de la station émettrice (cockpit, ventilation), sous la voix.</summary>
    Bed,

    /// <summary>Souffle de fond CONTINU de la porteuse VHF (background.wav), sous la transmission.</summary>
    Background,
}

/// <summary>
/// Échantillons audio RÉELS pour l'habillage radio, déposés par l'utilisateur dans
/// <c>%LOCALAPPDATA%\WilcoATC\radio\</c>.
///
/// POURQUOI DES FICHIERS ET PAS DE LA SYNTHÈSE : un déclic d'alternat, c'est un contact
/// métallique dans un boîtier — des résonances qu'on n'imite pas avec trois filtres. Une
/// respiration encore moins. La synthèse reste en repli pour l'alternat et la queue de
/// squelch (mieux que rien), mais la respiration et le fond de cockpit sont
/// EXCLUSIVEMENT échantillonnés : plutôt aucun son qu'un faux qui trahit.
///
/// NOMMAGE : le préfixe donne la catégorie, le reste est libre — ce qui permet plusieurs
/// variantes, tirées au hasard à chaque transmission. C'est ce qui évite l'effet « même
/// bruitage en boucle », qui trahit autant qu'un mauvais son.
///
///     keyup.wav       keyup-2.wav   keyup_yaesu.wav -> alternat
///     breath.wav      breath-long.wav               -> respiration
///     tail.wav        tail-3.wav                    -> queue de squelch
///     bed.wav         bed-cessna.wav                -> fond de cockpit
///     background.wav  background-vhf.wav            -> souffle de fond continu (porteuse VHF)
///
/// Tout est TOLÉRANT : dossier absent, fichier illisible, format exotique -> l'échantillon
/// est simplement ignoré et la chaîne continue.
/// </summary>
public sealed class RadioSampleRepository
{
    private readonly object _gate = new();

    // Chemin -> échantillons rééchantillonnés, par fréquence cible. Les fichiers sont
    // courts et peu nombreux : on garde tout en mémoire après le premier accès.
    private readonly Dictionary<(string Path, int Rate), float[]> _cache = new();

    private List<string>? _files;

    public string SamplesDir { get; }

    public RadioSampleRepository(string? samplesDir = null)
    {
        SamplesDir = string.IsNullOrWhiteSpace(samplesDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WilcoATC", "radio")
            : samplesDir!;
    }

    /// <summary>Nombre d'échantillons trouvés pour une catégorie (affiché dans les réglages).</summary>
    public int Count(RadioSampleKind kind) => FilesFor(kind).Count;

    /// <summary>Relit le dossier (après que l'utilisateur y a déposé des fichiers).</summary>
    public void Refresh()
    {
        lock (_gate) { _files = null; _cache.Clear(); }
    }

    /// <summary>
    /// Un échantillon de la catégorie demandée, rééchantillonné à <paramref name="rate"/>,
    /// ou null si aucun n'est disponible. Une VARIANTE est tirée au hasard à chaque appel.
    /// </summary>
    public float[]? Pick(RadioSampleKind kind, int rate, Random rng)
    {
        var candidates = FilesFor(kind);
        if (candidates.Count == 0) return null;

        string path = candidates[rng.Next(candidates.Count)];

        lock (_gate)
        {
            if (_cache.TryGetValue((path, rate), out var cached)) return cached;
        }

        float[]? loaded = Load(path, rate);
        if (loaded is null) return null;

        lock (_gate) _cache[(path, rate)] = loaded;
        return loaded;
    }

    private List<string> FilesFor(RadioSampleKind kind)
    {
        string prefix = kind switch
        {
            RadioSampleKind.KeyUp => "keyup",
            RadioSampleKind.Breath => "breath",
            RadioSampleKind.Tail => "tail",
            RadioSampleKind.Background => "background",
            _ => "bed",
        };

        return AllFiles()
            .Where(f => Path.GetFileNameWithoutExtension(f)
                            .StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private List<string> AllFiles()
    {
        lock (_gate)
        {
            if (_files is not null) return _files;

            var found = new List<string>();
            try
            {
                if (Directory.Exists(SamplesDir))
                    found.AddRange(Directory.EnumerateFiles(SamplesDir, "*.wav", SearchOption.TopDirectoryOnly));
            }
            catch (Exception ex)
            {
                FileLog.Write("[Radio] dossier d'échantillons illisible : " + ex.Message);
            }

            _files = found;
            return _files;
        }
    }

    private static float[]? Load(string path, int rate)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var audio = WavUtil.ReadMono(fs);
            if (audio.IsEmpty) return null;

            return audio.SampleRate == rate
                ? audio.Samples
                : Resample(audio.Samples, audio.SampleRate, rate);
        }
        catch (Exception ex)
        {
            // Un fichier corrompu ne doit jamais casser une transmission.
            FileLog.Write($"[Radio] échantillon ignoré ({Path.GetFileName(path)}) : {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Rééchantillonnage linéaire. Indispensable : un fichier en 44,1 kHz joué tel quel
    /// dans un flux à 22,05 kHz sortirait deux fois trop grave et deux fois trop long.
    /// L'interpolation linéaire suffit largement pour des bruitages courts.
    /// </summary>
    private static float[] Resample(float[] src, int fromRate, int toRate)
    {
        if (src.Length == 0 || fromRate <= 0 || toRate <= 0) return src;

        double ratio = (double)toRate / fromRate;
        int n = Math.Max(1, (int)(src.Length * ratio));
        var dst = new float[n];

        for (int i = 0; i < n; i++)
        {
            double pos = i / ratio;
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, src.Length - 1);
            double frac = pos - i0;
            dst[i] = (float)(src[i0] * (1 - frac) + src[i1] * frac);
        }
        return dst;
    }
}
