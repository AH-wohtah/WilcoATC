namespace FreqWatch.Common;

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

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
}
