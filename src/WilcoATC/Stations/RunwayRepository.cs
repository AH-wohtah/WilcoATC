using System.IO;

namespace WilcoATC.Stations;

/// <summary>Une extrémité de piste utilisable : son identifiant parlé et son orientation.</summary>
/// <param name="Ident">Désignateur tel qu'il est publié (« 08L », « 27R », « 04 »).</param>
/// <param name="HeadingTrue">Cap vrai d'atterrissage/décollage, en degrés.</param>
/// <param name="LengthFeet">Longueur de la piste (pieds) : départage à vent nul.</param>
/// <param name="Lat">Latitude du SEUIL (degrés), 0 si le fichier ne la donne pas.</param>
/// <param name="Lon">Longitude du seuil (degrés), 0 si absente.</param>
public sealed record RunwayEnd(string Ident, double HeadingTrue, int LengthFeet,
                               double Lat = 0, double Lon = 0)
{
    /// <summary>Le seuil est-il localisé ? Sans coordonnées, aucune distance n'est calculable.</summary>
    public bool HasPosition => Lat != 0 || Lon != 0;
}

/// <summary>
/// Pistes RÉELLES de chaque aéroport, lues depuis <c>data/runways.csv</c> (OurAirports,
/// domaine public — même source que les aéroports et les fréquences déjà embarqués).
///
/// POURQUOI CE FICHIER EXISTE : l'ATC déduisait la piste du CAP DE L'AVION (cap 240° ->
/// « piste 24 »). Sur un terrain dont les pistes sont 09/27, il annonçait donc une piste
/// qui n'existe pas — instruction impossible à suivre, et impossible à collationner. On ne
/// nomme désormais que des pistes lues dans les données ; à défaut, on dit « la piste en
/// service », qui reste vrai partout.
///
/// Chargement PARESSEUX et par aéroport : le fichier fait 48 000 lignes, on ne le parcourt
/// qu'au premier besoin, puis tout est en mémoire (~4 Mo) pour des appels instantanés.
/// </summary>
public sealed class RunwayRepository
{
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, List<RunwayEnd>>? _byIcao;

    public RunwayRepository(string? path = null)
        => _path = path ?? Path.Combine(AppContext.BaseDirectory, "data", "runways.csv");

    public bool IsLoaded => _byIcao is not null;

    /// <summary>Pistes publiées de cet aéroport (liste vide si inconnu ou données absentes).</summary>
    public IReadOnlyList<RunwayEnd> For(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao)) return Array.Empty<RunwayEnd>();
        var all = Load();
        return all.TryGetValue(icao!.Trim().ToUpperInvariant(), out var list)
            ? list
            : Array.Empty<RunwayEnd>();
    }

    /// <summary>Cette piste existe-t-elle vraiment sur ce terrain ? (comparaison sur le désignateur)</summary>
    public bool Exists(string? icao, string? runway)
    {
        if (string.IsNullOrWhiteSpace(runway)) return false;
        string wanted = Normalize(runway!);
        return For(icao).Any(r => Normalize(r.Ident) == wanted);
    }

    /// <summary>
    /// Piste EN SERVICE, choisie comme le ferait un vrai contrôleur :
    ///
    ///  1. face au vent — c'est la règle, et le simulateur nous donne le vent réel ;
    ///  2. vent faible ou inconnu : celle qui colle au cap de l'avion (il est aligné dessus,
    ///     ou en finale), à moins de 45° ;
    ///  3. sinon la plus longue, qui est la piste principale de presque tous les terrains.
    ///
    /// Renvoie null quand l'aéroport n'a aucune piste publiée : l'appelant dira « la piste
    /// en service » plutôt que d'inventer un numéro.
    /// </summary>
    public RunwayEnd? Active(string? icao, double windFromDeg, double windKnots, double aircraftHeadingDeg)
    {
        var ends = For(icao);
        if (ends.Count == 0) return null;

        // 1. Face au vent. Sous 4 nœuds, la direction n'a plus de sens (elle tourne au gré
        //    de la météo du simulateur) : on ne s'en sert pas pour trancher.
        if (windKnots >= 4 && windFromDeg > 0)
            return ends.OrderBy(r => AngleBetween(r.HeadingTrue, windFromDeg))
                       .ThenByDescending(r => r.LengthFeet)
                       .First();

        // 2. Cap de l'avion, s'il désigne clairement une piste.
        if (aircraftHeadingDeg > 0)
        {
            var aligned = ends.Where(r => AngleBetween(r.HeadingTrue, aircraftHeadingDeg) <= 45)
                              .OrderBy(r => AngleBetween(r.HeadingTrue, aircraftHeadingDeg))
                              .ThenByDescending(r => r.LengthFeet)
                              .FirstOrDefault();
            if (aligned is not null) return aligned;
        }

        // 3. La plus longue.
        return ends.OrderByDescending(r => r.LengthFeet).First();
    }

    /// <summary>
    /// Seuil de piste le plus proche d'un point, et sa distance en mètres. Null si le terrain
    /// n'a aucune piste localisée.
    ///
    /// C'est ce qui permet de reconnaître le POINT D'ARRÊT sans base de données de taxiways :
    /// un avion qui roule au départ et se retrouve à quelques centaines de mètres d'un seuil
    /// est, en pratique, en train d'y arriver.
    /// </summary>
    public (RunwayEnd End, double Meters)? NearestThreshold(string? icao, double lat, double lon)
    {
        (RunwayEnd End, double Meters)? best = null;

        foreach (var end in For(icao))
        {
            if (!end.HasPosition) continue;
            double m = Common.Geo.DistanceMeters(lat, lon, end.Lat, end.Lon);
            if (best is null || m < best.Value.Meters) best = (end, m);
        }

        return best;
    }

    /// <summary>Écart angulaire absolu entre deux caps, ramené à [0, 180].</summary>
    private static double AngleBetween(double a, double b)
    {
        double d = Math.Abs(((a - b) % 360 + 360) % 360);
        return d > 180 ? 360 - d : d;
    }

    private static string Normalize(string ident)
        => ident.Trim().ToUpperInvariant().TrimStart('0');

    private Dictionary<string, List<RunwayEnd>> Load()
    {
        if (_byIcao is not null) return _byIcao;

        lock (_gate)
        {
            if (_byIcao is not null) return _byIcao;

            var map = new Dictionary<string, List<RunwayEnd>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(_path))
                    foreach (var row in Csv.Read(_path))
                    {
                        // Une piste fermée ne sert plus : l'annoncer serait aussi faux que
                        // d'en inventer une.
                        if (row.TryGetDouble("closed", out double closed) && closed >= 1) continue;
                        if (!row.TryGet("airport_ident", out string icao) || icao.Length == 0) continue;

                        row.TryGetDouble("length_ft", out double lengthFt);

                        Add(map, icao, row, "le_ident", "le_heading_degT", (int)lengthFt,
                            "le_latitude_deg", "le_longitude_deg");
                        Add(map, icao, row, "he_ident", "he_heading_degT", (int)lengthFt,
                            "he_latitude_deg", "he_longitude_deg");
                    }
            }
            catch { /* données absentes ou illisibles -> « la piste en service » partout */ }

            _byIcao = map;
            return map;
        }
    }

    private static void Add(Dictionary<string, List<RunwayEnd>> map, string icao,
                            Csv.Row row, string identCol, string headingCol, int lengthFt,
                            string latCol, string lonCol)
    {
        if (!row.TryGet(identCol, out string ident) || ident.Length == 0) return;

        // Une hélisurface (« H1 ») n'est pas une piste sur laquelle on décolle en ligne :
        // on ne garde que les désignateurs qui commencent par un numéro de piste.
        if (!char.IsDigit(ident[0])) return;

        // Le cap vrai manque parfois ; on le déduit alors du numéro (piste 27 -> 270°),
        // ce qui est exact à quelques degrés près et suffit à trier.
        if (!row.TryGetDouble(headingCol, out double heading) || heading <= 0)
        {
            string digits = new(ident.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out int n) || n <= 0) return;
            heading = n * 10.0;
        }

        row.TryGetDouble(latCol, out double lat);
        row.TryGetDouble(lonCol, out double lon);

        string key = icao.Trim().ToUpperInvariant();
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<RunwayEnd>();
        list.Add(new RunwayEnd(ident.Trim(), heading, lengthFt, lat, lon));
    }
}
