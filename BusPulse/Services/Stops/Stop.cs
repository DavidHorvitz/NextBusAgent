namespace BusPulse.Services.Stops;

/// <summary><see cref="Id"/> is the Stride GTFS stop id (needed for ride queries); 0 when unknown.</summary>
public sealed record Stop(string Code, string Name, string? City, double Latitude, double Longitude, long Id = 0);

public sealed record NearbyStop(Stop Stop, double DistanceMeters);

public static class Geo
{
    private const double EarthRadiusMeters = 6_371_000;

    // Generous bounding box around Israel; anything outside is treated as invalid data.
    public static bool IsInIsrael(double latitude, double longitude) =>
        latitude is >= 29.3 and <= 33.5 && longitude is >= 34.2 and <= 35.95;

    public static double DistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * EarthRadiusMeters * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    /// <summary>Initial compass bearing from the first point to the second: 0 is north, 90 is east.</summary>
    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = ToRadians(lat1);
        var phi2 = ToRadians(lat2);
        var dLon = ToRadians(lon2 - lon1);
        var y = Math.Sin(dLon) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
        return (Math.Atan2(y, x) * 180 / Math.PI + 360) % 360;
    }

    public static IReadOnlyList<NearbyStop> FindNearby(
        IEnumerable<Stop> stops,
        double latitude,
        double longitude,
        int count,
        double maxMeters) =>
        stops
            .Select(stop => new NearbyStop(stop, DistanceMeters(latitude, longitude, stop.Latitude, stop.Longitude)))
            .Where(nearby => nearby.DistanceMeters <= maxMeters)
            .OrderBy(nearby => nearby.DistanceMeters)
            .Take(count)
            .ToArray();

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;
}
