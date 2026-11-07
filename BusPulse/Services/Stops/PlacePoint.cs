using BusPulse.Configuration;
using BusPulse.Services.Trips;

namespace BusPulse.Services.Stops;

/// <summary>Resolves a saved place to a point and to the stop nearest that point. Pure, no I/O.</summary>
public static class PlacePoint
{
    /// <summary>Coordinates win when present, otherwise the catalog stop with the place's code, otherwise null.</summary>
    public static (double Latitude, double Longitude)? Resolve(SavedPlace place, IEnumerable<Stop> stops)
    {
        if (place.Latitude is { } latitude && place.Longitude is { } longitude)
        {
            return (latitude, longitude);
        }

        if (place.StopCode.Length > 0)
        {
            var stop = stops.FirstOrDefault(candidate => candidate.Code == place.StopCode);
            if (stop is not null)
            {
                return (stop.Latitude, stop.Longitude);
            }
        }

        return null;
    }

    /// <summary>The closest stop with a Stride id within walking distance of the place's point, or null.</summary>
    public static Stop? NearestStop(SavedPlace place, IEnumerable<Stop> stops)
    {
        var all = stops as IReadOnlyCollection<Stop> ?? stops.ToArray();
        if (Resolve(place, all) is not { } point)
        {
            return null;
        }

        var nearest = Geo.FindNearby(all.Where(stop => stop.Id > 0), point.Latitude, point.Longitude, 1, TripPlannerService.MaxWalkMeters);
        return nearest.Count == 0 ? null : nearest[0].Stop;
    }
}
