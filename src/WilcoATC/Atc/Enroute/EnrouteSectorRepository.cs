using System.Globalization;
using System.IO;
using System.Text;

namespace WilcoATC.Atc.Enroute;

/// <summary>
/// Secteurs en-route installés (<c>%LOCALAPPDATA%\WilcoATC\enroute\sectors.txt</c>).
///
/// FORMAT, volontairement du texte à plat plutôt que du JSON : une ligne par secteur, lue
/// en un seul parcours. Le fichier fait plusieurs mégaoctets une fois le monde installé, et
/// un désérialiseur générique y passerait plusieurs secondes au démarrage.
///
///     nom|fréquenceHz|FL min|FL max|lat,lon;lat,lon;…
///
/// Les données ne sont PAS embarquées : elles se téléchargent à la demande (voir
/// <see cref="EnrouteSectorImporter"/>), pour que l'application ne redistribue rien.
/// </summary>
public sealed class EnrouteSectorRepository
{
    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, "sectors.txt");

    private IReadOnlyList<EnrouteSector>? _sectors;

    public EnrouteSectorRepository(string? dir = null)
    {
        Directory = string.IsNullOrWhiteSpace(dir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WilcoATC", "enroute")
            : dir!;
    }

    public bool IsInstalled => File.Exists(FilePath);

    /// <summary>Nombre de secteurs installés (0 si absent). Affiché dans les réglages.</summary>
    public int Count => Load().Count;

    /// <summary>Oublie le cache mémoire : le prochain accès relit le fichier.</summary>
    public void Invalidate() => _sectors = null;

    /// <summary>
    /// Contrôleur en-route dont le secteur contient ce point à cette altitude, ou null.
    /// À égalité, on retient le secteur le PLUS FIN en altitude : c'est le contrôleur le
    /// plus spécifique, celui qu'on aurait réellement en fréquence.
    /// </summary>
    public EnrouteSector? Find(double lat, double lon, double altitudeFeet)
    {
        int fl = (int)Math.Round(altitudeFeet / 100.0);

        EnrouteSector? best = null;
        foreach (var s in Load())
        {
            if (!s.Contains(lat, lon, fl)) continue;
            if (best is null || s.Thickness < best.Thickness) best = s;
        }
        return best;
    }

    private IReadOnlyList<EnrouteSector> Load()
    {
        if (_sectors is not null) return _sectors;

        var list = new List<EnrouteSector>();
        try
        {
            if (File.Exists(FilePath))
                foreach (var line in File.ReadLines(FilePath))
                {
                    var sector = Parse(line);
                    if (sector is not null) list.Add(sector);
                }
        }
        catch { /* fichier illisible -> aucun secteur, les replis habituels s'appliquent */ }

        _sectors = list;
        return list;
    }

    private static EnrouteSector? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] == '#') return null;

        var parts = line.Split('|');
        if (parts.Length < 5) return null;

        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double hz)) return null;
        if (!int.TryParse(parts[2], out int min) || !int.TryParse(parts[3], out int max)) return null;

        var points = new List<(double, double)>();
        foreach (var pair in parts[4].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int comma = pair.IndexOf(',');
            if (comma <= 0) continue;
            if (double.TryParse(pair[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                && double.TryParse(pair[(comma + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
                points.Add((lat, lon));
        }

        return points.Count < 3 ? null : new EnrouteSector(parts[0], hz, min, max, points);
    }

    /// <summary>Écrit la table réduite. Appelé par l'importateur, une fois le tri fait.</summary>
    public void Save(IEnumerable<EnrouteSector> sectors, string header)
    {
        System.IO.Directory.CreateDirectory(Directory);

        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(header);

        foreach (var s in sectors)
        {
            sb.Append(s.Name.Replace('|', ' ')).Append('|')
              .Append(s.FrequencyHz.ToString("F0", CultureInfo.InvariantCulture)).Append('|')
              .Append(s.MinFlightLevel).Append('|')
              .Append(s.MaxFlightLevel).Append('|');

            for (int i = 0; i < s.Points.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(s.Points[i].Lat.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
                  .Append(s.Points[i].Lon.ToString("F4", CultureInfo.InvariantCulture));
            }
            sb.AppendLine();
        }

        File.WriteAllText(FilePath, sb.ToString());
        Invalidate();
    }
}
