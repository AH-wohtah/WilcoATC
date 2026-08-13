using WilcoATC.Sim;

namespace WilcoATC.Atc.Atis;

/// <summary>
/// Ramène la météo AMBIANTE (celle relevée à l'avion) aux conditions DU TERRAIN — les
/// seules qu'un ATIS a le droit d'annoncer.
///
/// SimConnect ne sait mesurer qu'à l'endroit où se trouve l'appareil. En croisière, prendre
/// la mesure telle quelle donnerait « vent 270 degrés 85 nœuds, température moins 50 » : un
/// bulletin de piste absurde. Deux corrections classiques, et rien de plus :
///
///  • VENT — sous <see cref="AmbientIsSurfaceFeet"/> la mesure EST le vent de surface, on la
///    garde intacte. Au-dessus on applique la relation de couche limite : le vent de surface
///    vaut ≈ 55 % du vent gradient et ADONNE d'environ 30° (dans l'autre sens au sud de
///    l'équateur, la déviation de Coriolis y étant inversée). C'est une ESTIMATION, assumée
///    comme telle — mais un ordre de grandeur juste vaut mieux qu'un chiffre impossible ;
///  • TEMPÉRATURE — détendue au gradient standard (1,98 °C par 1 000 ft) jusqu'au sol.
///
/// La PRESSION n'est pas corrigée : « SEA LEVEL PRESSURE » est déjà réduite au niveau de la
/// mer par le simulateur, c'est directement le QNH.
///
/// Le vent est enfin converti en MAGNÉTIQUE : les pistes sont numérotées au magnétique, et
/// un ATIS annonce le vent dans le même repère que la piste qu'il donne.
/// </summary>
public static class AtisSurface
{
    /// <summary>Hauteur sol sous laquelle la mesure ambiante vaut pour du vent de surface.</summary>
    private const double AmbientIsSurfaceFeet = 4_000;

    private const double SurfaceWindFactor = 0.55;
    private const double SurfaceWindBackingDeg = 30;
    private const double IsaLapseCelsiusPerFoot = 1.98 / 1000.0;

    /// <summary>Vent de surface maximal annoncé (kt) — au-delà, l'estimation ne veut plus rien dire.</summary>
    private const double MaxSurfaceWindKnots = 60;

    /// <summary>Plage de QNH crédible (hPa). Hors de là, on considère la pression inconnue.</summary>
    private const double MinQnh = 850, MaxQnh = 1_100;

    public static AtisConditions Reduce(WeatherSnapshot w, double aglFeet, double latitudeDeg)
    {
        double agl = Math.Max(0, aglFeet);
        bool nearSurface = agl <= AmbientIsSurfaceFeet;

        double speed = nearSurface ? w.WindSpeedKnots : w.WindSpeedKnots * SurfaceWindFactor;

        // « Adonner » = tourner dans le sens anti-horaire dans l'hémisphère nord.
        double backing = nearSurface ? 0
            : latitudeDeg >= 0 ? -SurfaceWindBackingDeg : SurfaceWindBackingDeg;

        double magDir = Normalize(w.WindDirectionTrueDeg + backing - w.MagneticVariationDeg);
        double temp = w.TemperatureC + (nearSurface ? 0 : agl * IsaLapseCelsiusPerFoot);

        double qnh = w.SeaLevelPressureHpa;
        if (qnh < MinQnh || qnh > MaxQnh) qnh = 0; // inconnue -> l'annonce l'omettra

        return new AtisConditions(
            WindDirectionDeg: RoundToTen(magDir),
            WindSpeedKnots: (int)Math.Round(Math.Clamp(speed, 0, MaxSurfaceWindKnots)),
            VisibilityMeters: (int)Math.Round(Math.Clamp(w.VisibilityMeters, 0, 99_999)),
            TemperatureC: (int)Math.Round(Math.Clamp(temp, -60, 60)),
            QnhHectopascals: (int)Math.Round(qnh),
            Precipitation: w.Precipitation);
    }

    /// <summary>
    /// Piste la mieux orientée face à un vent donné (« 240 » -> « 24 »). Sans base de
    /// pistes, c'est la seule déduction possible — et c'est bien le critère qu'emploie un
    /// vrai terrain pour choisir sa piste en service.
    /// </summary>
    public static string RunwayFacing(int windMagDeg)
    {
        int n = (int)Math.Round(Normalize(windMagDeg) / 10.0, MidpointRounding.AwayFromZero);
        if (n <= 0 || n > 36) n = 36;   // 005° comme 355° -> piste 36
        return n.ToString("D2");
    }

    /// <summary>Direction ramenée dans [0, 360[.</summary>
    private static double Normalize(double deg) => ((deg % 360) + 360) % 360;

    /// <summary>Direction arrondie aux 10° comme dans un bulletin : 0 s'annonce « 360 ».</summary>
    private static int RoundToTen(double deg)
    {
        int rounded = (int)Math.Round(Normalize(deg) / 10.0, MidpointRounding.AwayFromZero) * 10;
        if (rounded is 0 or 360) return 360;
        return rounded;
    }
}
