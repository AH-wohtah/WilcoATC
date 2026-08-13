using System.Globalization;
using System.IO;
using WilcoATC.Common;

namespace WilcoATC.Stations;

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

    // Idents ayant AU MOINS une fréquence en bande COM aviation. Rempli au chargement, il
    // sert à écarter du choix d'aéroport les terrains « sans radio » (bases militaires,
    // pistes privées…) : c'est ce qui fait perdre EBMB (Melsbroek) face à EBBR à Bruxelles.
    private readonly HashSet<string> _airportsWithCom = new(StringComparer.OrdinalIgnoreCase);

    // ICAO présents dans l'overlay UTILISATEUR (corrections importées) : ils FONT AUTORITÉ, y
    // compris par-dessus les fréquences live du simulateur (voir SimStationResolver).
    private readonly HashSet<string> _userOverlayIcaos = new(StringComparer.OrdinalIgnoreCase);

    private bool _loaded;
    private bool _available;

    /// <summary>Overlay UTILISATEUR (corrections importées), inscriptible : %LOCALAPPDATA%\WilcoATC\frequencies-extra.csv.</summary>
    public static string UserOverlayPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WilcoATC", "frequencies-extra.csv");

    /// <summary>Cet aéroport a-t-il des fréquences CORRIGÉES par l'utilisateur (overlay importé) ?</summary>
    public bool IsUserOverride(string? icao)
    {
        EnsureLoaded();
        return !string.IsNullOrWhiteSpace(icao) && _userOverlayIcaos.Contains(icao!.Trim());
    }

    private const double FreqToleranceMhz = 0.006;
    private const double MaxDistanceMeters = 150_000;

    public OurAirportsStationResolver(string dataDir) => _dataDir = dataDir;

    // Résolveur hors-ligne : jamais de mise à jour live -> l'événement ne se déclenche pas.
    public event Action<string>? FrequenciesUpdated { add { } remove { } }

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

    public IReadOnlyList<AirportFrequency> ListFrequencies(string icao)
    {
        EnsureLoaded();
        if (!_available || string.IsNullOrWhiteSpace(icao)) return Array.Empty<AirportFrequency>();

        // Le CSV n'expose qu'un type TEXTE (« TWR », « CLNC DEL »…) — pas de type numérique.
        // Le filtrage hors-bande, la mise en libellé, la déduplication et le tri sont
        // factorisés dans ControllerTaxonomy (mêmes règles que la voie live SimConnect).
        var src = new List<(double Mhz, string? Name, int? SimType)>();
        foreach (var f in _freqs)
            if (f.AirportIdent.Equals(icao, StringComparison.OrdinalIgnoreCase))
                src.Add((f.Mhz, f.Label, null));

        return ControllerTaxonomy.BuildList(src);
    }

    /// <summary>Bande VHF aviation civile. Voir <see cref="ControllerTaxonomy.IsAviationComBand"/>.</summary>
    private static bool IsComBand(double mhz) => ControllerTaxonomy.IsAviationComBand(mhz);

    public string? NearestControlledAirportIcao(double lat, double lon, bool includeSmallFields = false)
    {
        EnsureLoaded();
        if (!_available) return null;

        // Aéroport utilisable comme terrain de destination. Condition commune aux deux
        // régimes : posséder une fréquence Tour, donc être réellement contrôlé — ça écarte
        // le petit terrain voisin sans fréquence, type Melsbroek/EBMB à côté de Bruxelles.
        //
        // Ce qui CHANGE selon les règles de vol, c'est la taille retenue :
        //
        //  • IFR : « large » ou « medium » seulement. Le jeu de données compte 42 685 petits
        //    terrains contre 1 173 grands, et beaucoup de petits ont une tour : sans ce
        //    filtre l'ATC envoyait le pilote « contacter » un aérodrome survolé au hasard.
        //    Entre deux candidats on pondère la distance — un grand aéroport un peu plus
        //    loin vaut mieux qu'un terrain moyen tout proche (facteur 2).
        //
        //  • VFR : tous les terrains, et SANS pondération. Le petit aérodrome écarté plus
        //    haut est exactement la destination d'un vol à vue ; l'envoyer à l'aéroport
        //    international 40 NM plus loin serait le bug symétrique de celui qu'on corrige.
        string? best = null;
        double bestScore = double.MaxValue;

        foreach (var f in _freqs)
        {
            if (MapType(f.Label) != ControllerType.Tower) continue;
            if (!_airports.TryGetValue(f.AirportIdent, out var ap)) continue;
            if (!includeSmallFields && ap.Kind == AirportClass.Other) continue;

            double d = Geo.DistanceMeters(lat, lon, ap.Lat, ap.Lon);
            if (d > MaxDistanceMeters) continue;

            double score = includeSmallFields
                ? d
                : d * (ap.Kind == AirportClass.Large ? 1.0 : 2.0);
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

    public string? OperationalAirport(string? nearestIcao, double lat, double lon)
    {
        EnsureLoaded();
        if (!_available) return nearestIcao;

        // Le terrain signalé par le simulateur publie déjà des fréquences -> on le respecte :
        // c'est bien celui où l'on se trouve. Ne JAMAIS le remplacer dans ce cas (sinon on
        // détournerait un pilote posé sur un petit terrain à radio vers l'international voisin).
        if (!string.IsNullOrWhiteSpace(nearestIcao) && _airportsWithCom.Contains(nearestIcao!))
            return nearestIcao;

        // Le plus proche n'a AUCUNE fréquence (base militaire, piste privée…). On cherche un
        // terrain à fréquences MITOYEN, borné à quelques km : assez pour couvrir le couple
        // base/aéroport qui se touchent (EBMB -> EBBR), trop serré pour sauter vers un grand
        // aéroport lointain quand on est réellement sur un terrain sans données.
        string? colocated = NearestAirportWithFrequencies(lat, lon, maxKm: 12.0);
        return colocated ?? nearestIcao;
    }

    /// <summary>
    /// Aéroport le plus proche AYANT au moins une fréquence COM publiée, dans un rayon donné.
    /// Une préférence de taille ADDITIVE et bornée (quelques centaines de mètres) départage
    /// deux terrains quasi co-localisés — le grand l'emporte — sans jamais primer sur un
    /// terrain nettement plus proche.
    /// </summary>
    private string? NearestAirportWithFrequencies(double lat, double lon, double maxKm)
    {
        double maxMeters = maxKm * 1000.0;
        string? best = null;
        double bestScore = double.MaxValue;

        foreach (var ident in _airportsWithCom)
        {
            if (!_airports.TryGetValue(ident, out var ap)) continue;

            double d = Geo.DistanceMeters(lat, lon, ap.Lat, ap.Lon);
            if (d > maxMeters) continue;

            double score = d + ap.Kind switch
            {
                AirportClass.Large => 0.0,
                AirportClass.Medium => 800.0,
                _ => 1800.0,
            };
            if (score >= bestScore) continue;
            bestScore = score;
            best = ident;
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

    // Classification texte -> type et libellé court : factorisées dans ControllerTaxonomy,
    // partagées avec la voie live SimConnect (source unique de vérité).
    private static ControllerType MapType(string? type) => ControllerTaxonomy.FromText(type);
    private static string TypeLabel(ControllerType t) => ControllerTaxonomy.ShortLabel(t);

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

                        // HORS BANDE = ÉCARTÉ, définitivement.
                        //
                        // OurAirports recense aussi le militaire : 2 600 entrées tombent hors
                        // de la bande aviation civile — 30 à 50 MHz, 138 à 144, au-delà de 225.
                        // Et rien ne les signale : « KDLF, GND, 138.75 » a le type d'une
                        // banale fréquence sol. Servie en transfert, elle envoie le pilote sur
                        // une fréquence que son poste ne peut PAS afficher — un A320 s'arrête à
                        // 136.975 — et la couverture ATC s'interrompt là, sans recours.
                        //
                        // Le filtre existait déjà pour l'AFFICHAGE (ControllerTaxonomy), mais
                        // pas pour cette liste-ci, celle des transferts. Les deux chemins
                        // doivent voir les mêmes données.
                        if (!IsComBand(mhz)) continue;

                        row.TryGet("type", out var type);
                        _freqs.Add(new Freq(mhz, ai, type));
                        _airportsWithCom.Add(ai);
                    }
                }

                // Overlays de fréquences RÉELLES (colonnes icao,type,mhz), qui complètent/corrigent
                // OurAirports là où le sim ET le CSV sont en retard sur l'AIP réel :
                //  1. l'overlay LIVRÉ (data/frequencies-extra.csv) ;
                //  2. l'overlay UTILISATEUR importé (%LOCALAPPDATA%\WilcoATC\frequencies-extra.csv),
                //     chargé en DERNIER, donc prioritaire — ce sont les corrections communautaires validées.
                LoadOverlay(FindFile("frequencies-extra.csv", "frequences-extra.csv"), isUserOverlay: false);
                LoadOverlay(UserOverlayPath, isUserOverlay: true);

                _available = _airports.Count > 0;
            }
            catch
            {
                _available = false;
            }
        }
    }

    // Charge un overlay de fréquences (icao,type,mhz). Il REMPLACE (ne complète pas) les entrées
    // OurAirports de ses ICAO — sinon l'arrondi 8,33 kHz ferait des quasi-doublons (« 118.605 »
    // OurAirports vs « 118.60 » AIP). L'overlay utilisateur mémorise en plus ses ICAO (autorité).
    private void LoadOverlay(string? path, bool isUserOverlay)
    {
        if (path is null || !File.Exists(path)) return;

        var overlay = new List<Freq>();
        var overlayIcaos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Csv.Read(path))
        {
            if (!row.TryGet("icao", out var ai)) continue;
            if (!row.TryGetDouble("mhz", out var mhz)) continue;
            ai = ai.Trim().ToUpperInvariant();
            if (ai.Length == 0) continue;

            // Même règle que pour la source principale : une correction communautaire mal
            // saisie ne doit pas pouvoir réintroduire une fréquence inaffichable.
            if (!IsComBand(mhz)) continue;

            row.TryGet("type", out var type);
            overlay.Add(new Freq(mhz, ai, type ?? ""));
            overlayIcaos.Add(ai);
            _airportsWithCom.Add(ai);
        }

        if (overlayIcaos.Count > 0)
            _freqs.RemoveAll(f => overlayIcaos.Contains(f.AirportIdent));
        _freqs.AddRange(overlay);

        if (isUserOverlay)
            foreach (var ic in overlayIcaos) _userOverlayIcaos.Add(ic);
    }

    /// <summary>Recharge tout (données + overlays) — utilisé après un import de fréquences.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _freqs.Clear();
            _airports.Clear();
            _airportsWithCom.Clear();
            _userOverlayIcaos.Clear();
            _available = false;
            _loaded = false;
        }
        EnsureLoaded();
    }

    /// <summary>
    /// Fusionne un CSV importé (colonnes <c>icao,type,mhz</c>) dans l'overlay utilisateur
    /// (dédoublonnage sur icao+type+mhz), réécrit le fichier, recharge, et renvoie le nombre de
    /// lignes NOUVELLES ajoutées.
    /// </summary>
    public int ImportOverlay(string csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath) || !File.Exists(csvPath)) return 0;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();

        // 1) Entrées déjà présentes dans l'overlay utilisateur (on les conserve).
        string userPath = UserOverlayPath;
        if (File.Exists(userPath))
            foreach (var row in Csv.Read(userPath))
                if (TryOverlayRow(row, out var ic, out var ty, out var m) && seen.Add(OverlayKey(ic, ty, m)))
                    lines.Add(OverlayLine(ic, ty, m));

        // 2) Lignes du fichier importé (celles qui n'existent pas encore).
        int added = 0;
        foreach (var row in Csv.Read(csvPath))
            if (TryOverlayRow(row, out var ic, out var ty, out var m) && seen.Add(OverlayKey(ic, ty, m)))
            {
                lines.Add(OverlayLine(ic, ty, m));
                added++;
            }

        // 3) Réécriture du fichier utilisateur + rechargement.
        Directory.CreateDirectory(Path.GetDirectoryName(userPath)!);
        using (var w = new StreamWriter(userPath, append: false))
        {
            w.WriteLine("icao,type,mhz");
            foreach (var l in lines) w.WriteLine(l);
        }
        Reload();
        return added;
    }

    private static bool TryOverlayRow(Csv.Row row, out string icao, out string type, out double mhz)
    {
        icao = ""; type = ""; mhz = 0;
        if (!row.TryGet("icao", out var ic) || string.IsNullOrWhiteSpace(ic)) return false;
        if (!row.TryGetDouble("mhz", out mhz)) return false;
        row.TryGet("type", out var ty);
        icao = ic.Trim().ToUpperInvariant();
        type = (ty ?? "").Trim();
        return icao.Length > 0;
    }

    private static string OverlayKey(string icao, string type, double mhz)
        => $"{icao}|{type.ToUpperInvariant()}|{mhz.ToString("0.000", CultureInfo.InvariantCulture)}";

    private static string OverlayLine(string icao, string type, double mhz)
        => $"{icao},{type},{mhz.ToString("0.###", CultureInfo.InvariantCulture)}";

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
