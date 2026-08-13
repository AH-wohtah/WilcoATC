using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace WilcoATC.Atc.Enroute;

/// <summary>
/// Télécharge et installe les secteurs de contrôle en-route, à la demande de l'utilisateur.
///
/// SOURCE : le projet <b>VATGlasses</b> (github.com/lennycolton/vatglasses-data), qui publie
/// les secteurs ACC du monde entier avec leurs vraies fréquences. Les données sont sous
/// licence <b>CC BY-NC-SA 4.0</b> : l'application ne les REDISTRIBUE PAS — c'est la machine
/// de l'utilisateur qui les récupère, à sa demande, et l'attribution est affichée dans les
/// réglages. C'est aussi pour ça qu'aucune table n'est embarquée dans le dépôt.
///
/// RÉDUCTION : on ne garde que ce qui sert à répondre « qui tient ce point du ciel, et sur
/// quelle fréquence » — les positions de type CTR, leur contour et leur tranche d'altitude.
/// Le reste (couleurs, aéroports, groupes, positions de tour) est jeté : 29 Mo de JSON
/// deviennent quelques mégaoctets de texte lu en un parcours.
/// </summary>
public sealed class EnrouteSectorImporter
{
    public const string SourceUrl =
        "https://github.com/lennycolton/vatglasses-data/archive/refs/heads/main.zip";

    public const string Attribution =
        "Secteurs en-route : VATGlasses (lennycolton/vatglasses-data), CC BY-NC-SA 4.0.";

    private readonly EnrouteSectorRepository _repo;

    public EnrouteSectorImporter(EnrouteSectorRepository repo) => _repo = repo;

    /// <summary>
    /// Télécharge, réduit et installe. Renvoie un message d'état prêt à afficher.
    /// <paramref name="progress"/> reçoit les étapes (téléchargement, lecture, écriture).
    /// </summary>
    public async Task<string> InstallAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        try
        {
            progress?.Report("Downloading en-route sectors…");

            byte[] zip;
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("WilcoATC");
                zip = await http.GetByteArrayAsync(SourceUrl, ct).ConfigureAwait(false);
            }

            progress?.Report("Reading sectors…");

            var sectors = new List<EnrouteSector>();
            using (var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();

                    // Seuls les fichiers de données comptent (le dépôt embarque aussi un
                    // classeur de propriétaires, des workflows, un README…).
                    if (!entry.FullName.Contains("/data/", StringComparison.Ordinal)) continue;
                    if (!entry.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                    using var stream = entry.Open();
                    using var mem = new MemoryStream();
                    await stream.CopyToAsync(mem, ct).ConfigureAwait(false);
                    mem.Position = 0;

                    try { ReadCountry(mem, sectors); }
                    catch { /* un pays malformé n'empêche pas les autres */ }
                }
            }

            if (sectors.Count == 0) return "No sector found in the downloaded data.";

            progress?.Report("Installing…");
            _repo.Save(sectors, Attribution);

            return $"{sectors.Count} en-route sectors installed. " + Attribution;
        }
        catch (OperationCanceledException) { return "Download cancelled."; }
        catch (Exception ex) { return "Could not install the sectors: " + ex.Message; }
    }

    /// <summary>
    /// Lit un fichier pays : les positions (indicatif + fréquence) puis les volumes, et
    /// n'émet que les secteurs tenus par une position EN-ROUTE.
    /// </summary>
    private static void ReadCountry(Stream json, List<EnrouteSector> into)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("positions", out var positions)) return;
        if (!root.TryGetProperty("airspace", out var airspace)) return;

        // id -> (indicatif parlé, hertz), pour les seules positions de contrôle en-route.
        var centers = new Dictionary<string, (string Name, double Hz)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in positions.EnumerateObject())
        {
            var v = p.Value;
            if (!v.TryGetProperty("type", out var type)) continue;
            if (!string.Equals(type.GetString(), "CTR", StringComparison.OrdinalIgnoreCase)) continue;

            if (!v.TryGetProperty("frequency", out var f)) continue;
            if (!double.TryParse(f.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double mhz))
                continue;
            if (mhz < 100 || mhz > 140) continue;   // hors bande VHF aéro -> donnée douteuse

            string name = v.TryGetProperty("callsign", out var cs) ? cs.GetString() ?? p.Name : p.Name;
            centers[p.Name] = (name, mhz * 1_000_000.0);
        }

        foreach (var space in airspace.EnumerateArray())
        {
            if (!space.TryGetProperty("owner", out var owners)) continue;
            if (!space.TryGetProperty("sectors", out var sectors)) continue;

            // « owner » va du contrôleur le plus SPÉCIFIQUE au plus regroupé. On retient le
            // premier qui soit un Centre : c'est celui qu'un pilote aurait réellement en
            // fréquence dans ce secteur, plutôt qu'une position de regroupement de nuit.
            (string Name, double Hz)? owner = null;
            foreach (var o in owners.EnumerateArray())
            {
                string? id = o.GetString();
                if (id is not null && centers.TryGetValue(id, out var found)) { owner = found; break; }
            }
            if (owner is null) continue;

            foreach (var sector in sectors.EnumerateArray())
            {
                int min = sector.TryGetProperty("min", out var mn) && mn.TryGetInt32(out int a) ? a : 0;
                int max = sector.TryGetProperty("max", out var mx) && mx.TryGetInt32(out int b) ? b : 660;
                if (!sector.TryGetProperty("points", out var points)) continue;

                var contour = new List<(double, double)>();
                foreach (var pt in points.EnumerateArray())
                {
                    if (pt.GetArrayLength() < 2) continue;
                    double? lat = Dms(pt[0].GetString(), 2);
                    double? lon = Dms(pt[1].GetString(), 3);
                    if (lat is not null && lon is not null) contour.Add((lat.Value, lon.Value));
                }

                if (contour.Count >= 3)
                    into.Add(new EnrouteSector(owner.Value.Name, owner.Value.Hz, min, max, contour));
            }
        }
    }

    /// <summary>
    /// Coordonnée VATGlasses -> degrés décimaux. Les latitudes s'écrivent DDMMSS (6 chiffres)
    /// et les longitudes DDDMMSS (7), le signe moins marquant l'ouest et le sud :
    /// « 493622 » -> 49,606° et « -0001500 » -> -0,25°.
    /// </summary>
    private static double? Dms(string? raw, int degreeDigits)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string s = raw!.Trim();
        double sign = 1;
        if (s[0] == '-') { sign = -1; s = s[1..]; }
        else if (s[0] == '+') s = s[1..];

        // Certains fichiers omettent le zéro de tête : on complète pour garder l'alignement
        // degrés / minutes / secondes, sinon « 14833 » se lirait comme 148 degrés.
        s = s.PadLeft(degreeDigits + 4, '0');
        if (s.Length < degreeDigits + 4) return null;

        if (!int.TryParse(s[..degreeDigits], out int deg)) return null;
        if (!int.TryParse(s.Substring(degreeDigits, 2), out int minutes)) return null;
        if (!int.TryParse(s.Substring(degreeDigits + 2, 2), out int seconds)) return null;

        return sign * (deg + minutes / 60.0 + seconds / 3600.0);
    }
}
