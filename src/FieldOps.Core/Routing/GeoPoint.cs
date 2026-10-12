namespace FieldOps.Core.Routing;

/// <summary>A latitude/longitude pair in decimal degrees.</summary>
public readonly record struct GeoPoint(double Lat, double Lng);

public static class Distance
{
    private const double EarthRadiusKm = 6371.0;

    /// <summary>Great-circle (straight-line) distance between two points, in kilometres.</summary>
    public static double Km(GeoPoint a, GeoPoint b)
    {
        double dLat = ToRadians(b.Lat - a.Lat);
        double dLng = ToRadians(b.Lng - a.Lng);
        double h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(ToRadians(a.Lat)) * Math.Cos(ToRadians(b.Lat))
                 * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
    }

    /// <summary>Length of the open path start -> first stop -> ... -> last stop.</summary>
    public static double PathKm(GeoPoint start, IEnumerable<GeoPoint> stops)
    {
        double total = 0;
        var current = start;
        foreach (var stop in stops)
        {
            total += Km(current, stop);
            current = stop;
        }
        return total;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
