namespace WilcoATC.Common;

/// <summary>Utilitaires géographiques partagés.</summary>
public static class Geo
{
    private const double EarthRadiusMeters = 6_371_000;

    /// <summary>Distance grand-cercle (haversine) entre deux points, en mètres.</summary>
    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = Deg2Rad(lat2 - lat1);
        double dLon = Deg2Rad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(Deg2Rad(lat1)) * Math.Cos(Deg2Rad(lat2))
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return EarthRadiusMeters * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>
    /// Relèvement VRAI du point 1 vers le point 2, en degrés dans [0, 360[.
    ///
    /// Sert à distinguer un appareil qui SE PRÉSENTE à la piste d'un appareil qui vient d'en
    /// décoller : les deux sont alignés sur le même axe, au même endroit, au même cap — seul
    /// le sens dans lequel se trouve le terrain les sépare.
    /// </summary>
    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double φ1 = Deg2Rad(lat1), φ2 = Deg2Rad(lat2);
        double dλ = Deg2Rad(lon2 - lon1);

        double y = Math.Sin(dλ) * Math.Cos(φ2);
        double x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(dλ);

        double deg = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (deg % 360 + 360) % 360;
    }

    /// <summary>
    /// Écart ANGULAIRE le plus court entre deux caps, dans [0, 180]. Sans ce repliement, un
    /// cap au 359 et un cap au 001 sembleraient éloignés de 358 degrés au lieu de 2.
    /// </summary>
    public static double AngleDifference(double a, double b)
    {
        double d = Math.Abs((a - b) % 360);
        return d > 180 ? 360 - d : d;
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
}
