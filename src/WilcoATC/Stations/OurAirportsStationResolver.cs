using System.IO;
using FreqWatch.Common;

namespace FreqWatch.Stations;

/// <summary>
/// (Stretch) Données OurAirports (airports.csv + airport-frequencies.csv) dans le
/// dossier <c>data/</c>. Fournit le nom de station, le TYPE de contrôleur (pour la
/// validation ATC) et la recherche de fréquence par type.
///
/// Entièrement isolé : fichiers absents/illisibles -> tout renvoie null.
/// </summary>
public sealed class OurAirportsStationResolver : IStationResolver
{
    private sealed record Freq(double Mhz, string AirportIdent, string Label);
    private sealed record Airport(string Name, double Lat, double Lon);

    private readonly string _dataDir;
    private readonly object _gate = new();

    private readonly List<Freq> _freqs = new();
    private readonly Dictionary<string, Airport> _airports = new(StringComparer.OrdinalIgnoreCase);

    private bool _loaded;
    private bool _available;

    private const double FreqToleranceMhz = 0.006;
    private const double MaxDistanceMeters = 150_000;

    public OurAirportsStationResolver(string dataDir) => _dataDir = dataDir;

    public string? Resolve(double activeHz, double lat, double lon)
    {
        var (info, _) = ResolveNearest(activeHz, lat, lon);
        if (info is null) return null;
        return string.IsNullOrWhiteSpace(TypeLabel(info.Controller))
            ? info.Name
            : $"{info.Name} · {TypeLabel(info.Controller)}";
    }

    public StationInfo? ResolveStation(double activeHz, double lat, double lon)
        => ResolveNearest(activeHz, lat, lon).Info;

    public string? LookupAirportName(string icao)
    {
        EnsureLoaded();
        if (!_available || string.IsNullOrWhiteSpace(icao)) return null;
        return _airports.TryGetValue(icao, out var ap) ? ap.Name : null;
    }

    public string? NearestControlledAirportIcao(double lat, double lon)
    {
        EnsureLoaded();
        if (!_available) return null;

        // Aéroport CONTRÔLÉ = celui qui possède une fréquence Tour dans les données. Évite
        // qu'un petit terrain voisin SANS fréquence (ex. Melsbroek/EBMB à côté de Bruxelles)
        // soit retenu pour un transfert à la place du vrai aéroport (EBBR).
        string? best = null;
        double bestDist = double.MaxValue;
        foreach (var f in _freqs)
        {
            if (MapType(f.Label) != ControllerType.Tower) continue;
            if (!_airports.TryGetValue(f.AirportIdent, out var ap)) continue;
            double d = Geo.DistanceMeters(lat, lon, ap.Lat, ap.Lon);
            if (d > MaxDistanceMeters || d >= bestDist) continue;
            bestDist = d;
            best = f.AirportIdent;
        }
        return best;
    }

    public double? FindFrequencyHz(string icao, ControllerType controller)
    {
        EnsureLoaded();
        if (!_available || string.IsNullOrWhiteSpace(icao)) return null;
        foreach (var f in _freqs)
        {
            if (!f.AirportIdent.Equals(icao, StringComparison.OrdinalIgnoreCase)) continue;
            if (MapType(f.Label) == controller) return f.Mhz * 1_000_000.0;
        }
        return null;
    }

    private (StationInfo? Info, double Dist) ResolveNearest(double activeHz, double lat, double lon)
    {
        EnsureLoaded();
        if (!_available || activeHz < 1_000_000) return (null, double.MaxValue);

        double mhz = activeHz / 1_000_000.0;
        StationInfo? best = null;
        double bestDist = double.MaxValue;

        foreach (var f in _freqs)
        {
            if (Math.Abs(f.Mhz - mhz) > FreqToleranceMhz) continue;
            if (!_airports.TryGetValue(f.AirportIdent, out var ap)) continue;

            double d = Geo.DistanceMeters(lat, lon, ap.Lat, ap.Lon);
            if (d > MaxDistanceMeters || d >= bestDist) continue;
            bestDist = d;
            best = new StationInfo(ap.Name, MapType(f.Label));
        }
        return (best, bestDist);
    }

    // OurAirports "type" -> ControllerType.
    private static ControllerType MapType(string? type)
    {
        string t = (type ?? "").Trim().ToUpperInvariant();
        return t switch
        {
            "CLD" or "CLR" or "DEL" or "DELIVERY" or "CLEARANCE" => ControllerType.Clearance,
            "GND" or "GROUND" => ControllerType.Ground,
            "TWR" or "TOWER" => ControllerType.Tower,
            "APP" or "APPROACH" => ControllerType.Approach,
            "DEP" or "DEPARTURE" => ControllerType.Departure,
            "CTR" or "CTL" or "CENTER" or "CENTRE" => ControllerType.Center,
            "ATIS" or "AWOS" or "ASOS" => ControllerType.Atis,
            _ => ControllerType.Unknown,
        };
    }

    private static string TypeLabel(ControllerType t) => t switch
    {
        ControllerType.Clearance => "CLR",
        ControllerType.Ground => "GND",
        ControllerType.Tower => "TWR",
        ControllerType.Approach => "APP",
        ControllerType.Departure => "DEP",
        ControllerType.Center => "CTR",
        ControllerType.Atis => "ATIS",
        _ => "",
    };

    private void EnsureLoaded()
    {
        if (_loaded) return;
        lock (_gate)
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                // Noms de fichiers tolérants (OurAirports standard OU variantes « world-… »).
                string? airportsPath = FindFile("airports.csv", "world-airports.csv");
                string? freqsPath = FindFile("airport-frequencies.csv", "world-airport-frequencies.csv",
                                             "airports-frequencies.csv", "world-airport_frequencies.csv");
                if (airportsPath is null) return; // sans aéroports, rien à faire

                foreach (var row in Csv.Read(airportsPath))
                {
                    if (!row.TryGet("ident", out var ident)) continue;
                    if (!row.TryGetDouble("latitude_deg", out var la)) continue;
                    if (!row.TryGetDouble("longitude_deg", out var lo)) continue;
                    // On préfère le NOM officiel (« Brussels Airport » -> parlé « Brussels » après
                    // nettoyage), plus fiable que la commune (« Zaventem »/« Steenokkerzeel » à côté
                    // de Bruxelles). Repli sur la commune, puis l'ICAO.
                    row.TryGet("name", out var name);
                    row.TryGet("municipality", out var muni);
                    string display = !string.IsNullOrWhiteSpace(name) ? name
                                   : !string.IsNullOrWhiteSpace(muni) ? muni : ident;
                    _airports[ident] = new Airport(display, la, lo);
                }

                // Le fichier des fréquences est optionnel : sans lui, seuls les NOMS d'aéroport
                // marchent (pas de fréquences ni de type de contrôleur).
                if (freqsPath is not null)
                {
                    foreach (var row in Csv.Read(freqsPath))
                    {
                        if (!row.TryGet("airport_ident", out var ai)) continue;
                        if (!row.TryGetDouble("frequency_mhz", out var mhz)) continue;
                        row.TryGet("type", out var type);
                        _freqs.Add(new Freq(mhz, ai, type));
                    }
                }

                _available = _airports.Count > 0;
            }
            catch
            {
                _available = false;
            }
        }
    }

    // Premier fichier existant parmi les noms candidats (dans le dossier data).
    private string? FindFile(params string[] names)
    {
        foreach (var n in names)
        {
            string p = Path.Combine(_dataDir, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
