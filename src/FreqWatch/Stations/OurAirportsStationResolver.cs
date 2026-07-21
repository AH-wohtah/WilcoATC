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
    private sealed record Airport(string Name, double Lat, double Lon, AirportClass Kind);

    /// <summary>Catégorie OurAirports, réduite à ce qui nous sert pour un transfert IFR.</summary>
    private enum AirportClass { Other, Medium, Large }

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

        // Aéroport utilisable pour un transfert IFR. DEUX conditions :
        //
        //  • il possède une fréquence Tour (donc il est réellement contrôlé) — ça écarte le
        //    petit terrain voisin sans fréquence, type Melsbroek/EBMB à côté de Bruxelles ;
        //  • il est de catégorie « large » ou « medium ». Le jeu de données compte 42 685
        //    petits terrains contre 1 173 grands, et beaucoup de petits ont une tour : sans
        //    ce filtre, l'ATC envoyait le pilote « contacter » un aérodrome survolé, ce qui
        //    n'a aucun sens en IFR.
        //
        // Entre deux candidats, on pondère la distance : un grand aéroport un peu plus loin
        // vaut mieux qu'un terrain moyen tout proche (facteur 2), sans pour autant expédier
        // le pilote à l'autre bout du rayon de recherche.
        string? best = null;
        double bestScore = double.MaxValue;

        foreach (var f in _freqs)
        {
            if (MapType(f.Label) != ControllerType.Tower) continue;
            if (!_airports.TryGetValue(f.AirportIdent, out var ap)) continue;
            if (ap.Kind == AirportClass.Other) continue;

            double d = Geo.DistanceMeters(lat, lon, ap.Lat, ap.Lon);
            if (d > MaxDistanceMeters) continue;

            double score = d * (ap.Kind == AirportClass.Large ? 1.0 : 2.0);
            if (score >= bestScore) continue;

            bestScore = score;
            best = f.AirportIdent;
        }
        return best;
    }

    public (double Lat, double Lon)? AirportPosition(string icao)
    {
        EnsureLoaded();
        if (!_available || string.IsNullOrWhiteSpace(icao)) return null;
        return _airports.TryGetValue(icao, out var ap) ? (ap.Lat, ap.Lon) : null;
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

    public double? FindNearestFrequencyHz(ControllerType controller, double lat, double lon, double maxKm)
    {
        EnsureLoaded();
        if (!_available) return null;

        double maxMeters = maxKm * 1000.0;
        double? best = null;
        double bestDist = double.MaxValue;

        foreach (var f in _freqs)
        {
            if (MapType(f.Label) != controller) continue;
            if (!_airports.TryGetValue(f.AirportIdent, out var ap)) continue;

            double d = Geo.DistanceMeters(lat, lon, ap.Lat, ap.Lon);
            if (d > maxMeters || d >= bestDist) continue;

            bestDist = d;
            best = f.Mhz * 1_000_000.0;
        }
        return best;
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
    //
    // Le jeu de données est BEAUCOUP moins normalisé qu'il n'y paraît : à côté des codes
    // courts on trouve du texte libre (« Melbourne Centre », « Area Control », « APP/DEP »,
    // « CLNC DEL »…). Surtout, le centre en-route s'y écrit « CNTR » (1211 entrées) et
    // « ACC » (157) — et non « CTR », qui ne compte que 23 entrées. Ne reconnaître que
    // « CTR » revenait à ne JAMAIS trouver de fréquence Centre.
    //
    // D'où deux étages : les codes exacts, puis une reconnaissance par sous-chaîne pour le
    // texte libre. L'ordre du second étage compte (« Ground Control » ne doit pas devenir
    // un centre).
    private static ControllerType MapType(string? type)
    {
        string t = (type ?? "").Trim().ToUpperInvariant();
        if (t.Length == 0) return ControllerType.Unknown;

        switch (t)
        {
            case "CLD" or "CLR" or "DEL" or "CLRN" or "CLRD" or "DELIVERY" or "CLEARANCE"
                 or "CLNC DEL" or "CLR DLVR":
                return ControllerType.Clearance;
            case "GND" or "GROUND":
                return ControllerType.Ground;
            case "TWR" or "TOWER":
                return ControllerType.Tower;
            case "APP" or "APPROACH" or "ARR" or "DIR" or "RDR" or "RADAR":
                return ControllerType.Approach;
            case "DEP" or "DEPARTURE" or "DEPARTURES" or "DEPT":
                return ControllerType.Departure;
            case "CNTR" or "CTR" or "CTL" or "CTRL" or "ACC" or "ARTC"
                 or "CENTER" or "CENTRE" or "CONTROL":
                return ControllerType.Center;
            case "ATIS" or "AWOS" or "ASOS":
                return ControllerType.Atis;
        }

        if (t.Contains("CENTER") || t.Contains("CENTRE") || t.Contains("CTR")) return ControllerType.Center;
        if (t.Contains("DEP")) return ControllerType.Departure;      // « APP/DEP » -> l'un ou l'autre convient
        if (t.Contains("APP")) return ControllerType.Approach;
        if (t.Contains("DEL") || t.Contains("CLNC") || t.Contains("CLR")) return ControllerType.Clearance;
        if (t.Contains("GROUND")) return ControllerType.Ground;
        if (t.Contains("TWR") || t.Contains("TOWER")) return ControllerType.Tower;
        if (t.Contains("ATIS")) return ControllerType.Atis;

        return ControllerType.Unknown;
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

                    row.TryGet("type", out var kind);
                    var cls = (kind ?? "").Trim().ToLowerInvariant() switch
                    {
                        "large_airport" => AirportClass.Large,
                        "medium_airport" => AirportClass.Medium,
                        _ => AirportClass.Other,
                    };

                    _airports[ident] = new Airport(display, la, lo, cls);
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
