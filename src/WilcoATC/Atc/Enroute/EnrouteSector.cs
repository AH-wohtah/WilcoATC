namespace WilcoATC.Atc.Enroute;

/// <summary>
/// Un secteur de contrôle EN-ROUTE (ACC / Centre) : un volume d'espace aérien, sa tranche
/// d'altitude, et le contrôleur qui le tient.
///
/// POURQUOI UN VOLUME ET PAS UN AÉROPORT : le Centre est un service de SECTEUR. Il ne se
/// rattache à aucun terrain — c'est précisément pourquoi la recherche « le Centre de mon
/// aéroport » ne donnait jamais rien hors d'Amérique du Nord, où les données OurAirports
/// rattachent (par convention locale) les fréquences ARTCC aux petits terrains survolés.
/// </summary>
/// <param name="Name">Indicatif parlé du contrôleur (« Bordeaux Control »).</param>
/// <param name="FrequencyHz">Fréquence en hertz.</param>
/// <param name="MinFlightLevel">Plancher du secteur, en niveau de vol.</param>
/// <param name="MaxFlightLevel">Plafond du secteur, en niveau de vol.</param>
/// <param name="Points">Contour, en degrés décimaux (latitude, longitude).</param>
public sealed record EnrouteSector(
    string Name,
    double FrequencyHz,
    int MinFlightLevel,
    int MaxFlightLevel,
    IReadOnlyList<(double Lat, double Lon)> Points)
{
    // Rectangle englobant, calculé une fois : il rejette l'immense majorité des secteurs
    // sans dérouler leur contour. Sans lui, chaque interrogation testerait des milliers de
    // polygones point par point.
    private readonly double _minLat = Points.Count == 0 ? 0 : Points.Min(p => p.Lat);
    private readonly double _maxLat = Points.Count == 0 ? 0 : Points.Max(p => p.Lat);
    private readonly double _minLon = Points.Count == 0 ? 0 : Points.Min(p => p.Lon);
    private readonly double _maxLon = Points.Count == 0 ? 0 : Points.Max(p => p.Lon);

    /// <summary>Épaisseur de la tranche : sert à préférer le secteur le PLUS SPÉCIFIQUE.</summary>
    public int Thickness => Math.Max(1, MaxFlightLevel - MinFlightLevel);

    /// <summary>Le point (et son niveau de vol) tombe-t-il dans ce secteur ?</summary>
    public bool Contains(double lat, double lon, int flightLevel)
    {
        if (flightLevel < MinFlightLevel || flightLevel > MaxFlightLevel) return false;
        if (lat < _minLat || lat > _maxLat || lon < _minLon || lon > _maxLon) return false;
        return InPolygon(lat, lon);
    }

    /// <summary>
    /// Lancer de rayon. Le contour est donné en degrés : à l'échelle d'un secteur ACC, une
    /// géométrie plane est exacte à la précision utile. Un secteur à cheval sur l'antiméridien
    /// (Pacifique) serait mal jugé — aucun ACC européen n'est concerné, et le repli habituel
    /// (VATSIM, puis fréquence saisie) reprend la main.
    /// </summary>
    private bool InPolygon(double lat, double lon)
    {
        bool inside = false;
        for (int i = 0, j = Points.Count - 1; i < Points.Count; j = i++)
        {
            var (yi, xi) = Points[i];
            var (yj, xj) = Points[j];

            if (yi > lat != yj > lat &&
                lon < (xj - xi) * (lat - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }
}
