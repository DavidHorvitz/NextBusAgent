using BusPulse.Services.LiveData;
using BusPulse.Services.Stops;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Trips;

public enum TripTimeMode
{
    Now,
    DepartAt,
    ArriveBy
}

public enum TripPlanStatus
{
    Ok,
    NoStopsNearOrigin,
    NoStopsNearDestination,
    NoOptions
}

public sealed record TripRequest(
    double OriginLatitude,
    double OriginLongitude,
    double DestinationLatitude,
    double DestinationLongitude,
    TripTimeMode Mode = TripTimeMode.Now,
    DateTimeOffset? TargetTime = null,
    string? PreferredLine = null,
    int BufferMinutes = 1,
    long? WatchRideId = null);

public sealed record TripOption(
    long RideId,
    string Line,
    string? Operator,
    string? FromEndpoint,
    string? ToEndpoint,
    Stop OriginStop,
    double WalkToStopMeters,
    TimeSpan WalkToStop,
    Stop DestinationStop,
    double WalkFromStopMeters,
    TimeSpan WalkFromStop,
    DateTimeOffset BusDeparture,
    DateTimeOffset BusArrival,
    DateTimeOffset LeaveBy,
    DateTimeOffset ArriveAt,
    bool IsLive,
    ArrivalSource Source,
    DateTimeOffset ObservedAt);

public sealed record TripPlan(TripPlanStatus Status, IReadOnlyList<TripOption> Options);

public interface ITripPlannerService
{
    Task<TripPlan> PlanAsync(TripRequest request, CancellationToken cancellationToken = default);
}

public static class Walking
{
    private const double SpeedMetersPerSecond = 1.25;
    private const double DetourFactor = 1.25;

    /// <summary>Walking time for a straight-line distance, allowing for streets not being straight.</summary>
    public static TimeSpan Estimate(double straightLineMeters) =>
        TimeSpan.FromSeconds(Math.Max(0, straightLineMeters) * DetourFactor / SpeedMetersPerSecond);
}

/// <summary>
/// Direct (no transfer) trips. Only lines that call at a stop near the origin and later at a stop near the destination
/// are returned, which is also the regional filter: line 167 in Bnei Brak never matches a destination in Haifa.
/// </summary>
public sealed class TripPlannerService(
    IStopCatalog catalog,
    IRideScheduleSource schedule,
    ILiveDataAggregator aggregator,
    IClock clock,
    ILogger<TripPlannerService> logger) : ITripPlannerService
{
    public const double MaxWalkMeters = 800;
    private const int OriginStopCount = 5;
    private const int DestinationStopCount = 6;
    private const int MaxOptions = 8;
    private static readonly TimeSpan SearchWindow = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan RideLookAhead = TimeSpan.FromHours(3);
    private static readonly TimeSpan LiveMatchTolerance = TimeSpan.FromMinutes(15);

    /// <summary>Longest ride considered for arrive-by; departures earlier than target minus this cannot matter.</summary>
    private static readonly TimeSpan MaxRideDuration = TimeSpan.FromHours(3);

    /// <summary>How far back to look for a watched ride whose scheduled departure has passed but which may still be coming.</summary>
    private static readonly TimeSpan WatchLookBehind = TimeSpan.FromMinutes(30);

    /// <summary>Ride ids per destination query (the source rejects longer lists).</summary>
    private const int RideIdBatch = 80;

    public async Task<TripPlan> PlanAsync(TripRequest request, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var stops = await catalog.GetStopsAsync(cancellationToken);
        var originStops = Geo.FindNearby(stops.Where(stop => stop.Id > 0), request.OriginLatitude, request.OriginLongitude, OriginStopCount, MaxWalkMeters);
        if (originStops.Count == 0)
        {
            return new TripPlan(TripPlanStatus.NoStopsNearOrigin, []);
        }

        var destinationStops = Geo.FindNearby(stops.Where(stop => stop.Id > 0), request.DestinationLatitude, request.DestinationLongitude, DestinationStopCount, MaxWalkMeters);
        if (destinationStops.Count == 0)
        {
            return new TripPlan(TripPlanStatus.NoStopsNearDestination, []);
        }

        var target = request.TargetTime;
        var leaveEarliest = request.Mode == TripTimeMode.DepartAt && target is not null && target > now ? target.Value : now;
        var (from, to) = request.Mode switch
        {
            TripTimeMode.ArriveBy when target is not null => (Max(now, target.Value - MaxRideDuration), target.Value),
            _ => (leaveEarliest, leaveEarliest + SearchWindow)
        };

        // A watched ride is kept even after its scheduled departure: it may be late and still catchable.
        var watchRide = request.WatchRideId;
        var departuresFrom = watchRide is null ? from : from - WatchLookBehind;

        var buffer = TimeSpan.FromMinutes(Math.Clamp(request.BufferMinutes, 0, 15));
        var candidates = new List<(RideStop Departure, NearbyStop Origin)>();
        foreach (var origin in originStops)
        {
            var departures = await schedule.GetStopDeparturesAsync(origin.Stop.Id, departuresFrom, to, cancellationToken);
            candidates.AddRange(departures
                .Where(departure => string.IsNullOrWhiteSpace(request.PreferredLine) ||
                    string.Equals(departure.Line, request.PreferredLine.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(departure => (departure, origin)));
        }

        if (candidates.Count == 0)
        {
            return new TripPlan(TripPlanStatus.NoOptions, []);
        }

        var rideIds = candidates.Select(c => c.Departure.RideId).Distinct().ToArray();
        var destinationStopIds = destinationStops.Select(d => d.Stop.Id).ToArray();
        var destinationRows = new List<RideStop>();
        foreach (var batch in rideIds.Chunk(RideIdBatch))
        {
            destinationRows.AddRange(await schedule.GetRideStopsAtAsync(batch, destinationStopIds, departuresFrom, to + RideLookAhead, cancellationToken));
        }

        var live = request.Mode == TripTimeMode.Now || watchRide is not null
            ? await LoadLiveAsync(originStops, cancellationToken)
            : new Dictionary<string, List<Arrival>>();

        var options = new List<TripOption>();
        foreach (var (departure, origin) in candidates)
        {
            var arrival = destinationRows
                .Where(row => row.RideId == departure.RideId && row.Sequence > departure.Sequence)
                .Select(row => (Row: row, Stop: destinationStops.First(d => d.Stop.Id == row.StopId)))
                .OrderBy(match => match.Row.Time + Walking.Estimate(match.Stop.DistanceMeters))
                .Cast<(RideStop Row, NearbyStop Stop)?>()
                .FirstOrDefault();
            if (arrival is null)
            {
                continue;
            }

            var (destinationRow, destination) = arrival.Value;
            var liveMatch = MatchLive(live, origin.Stop.Code, departure);
            var busDeparture = liveMatch?.Eta ?? departure.Time;
            var rideDuration = destinationRow.Time - departure.Time;
            var busArrival = busDeparture + rideDuration;
            var walkToStop = Walking.Estimate(origin.DistanceMeters);
            var walkFromStop = Walking.Estimate(destination.DistanceMeters);
            var leaveBy = busDeparture - walkToStop - buffer;
            var arriveAt = busArrival + walkFromStop;

            var isWatched = departure.RideId == watchRide;
            if (!isWatched && leaveBy < leaveEarliest - TimeSpan.FromSeconds(30))
            {
                continue;
            }

            if (!isWatched && departure.Time < from)
            {
                continue;
            }

            if (!isWatched && request.Mode == TripTimeMode.ArriveBy && target is not null && arriveAt > target)
            {
                continue;
            }

            options.Add(new TripOption(
                departure.RideId,
                departure.Line,
                departure.Agency,
                RouteEndpoints.Origin(departure.RouteLongName),
                RouteEndpoints.Destination(departure.RouteLongName),
                origin.Stop,
                origin.DistanceMeters,
                walkToStop,
                destination.Stop,
                destination.DistanceMeters,
                walkFromStop,
                busDeparture,
                busArrival,
                leaveBy,
                arriveAt,
                liveMatch is not null,
                liveMatch?.Source ?? ArrivalSource.Schedule,
                liveMatch?.ObservedAt ?? now));
        }

        // One option per ride: keep the one that gets there soonest.
        var best = options
            .GroupBy(option => option.RideId)
            .Select(group => group.OrderBy(option => option.ArriveAt).First());
        var ordered = request.Mode == TripTimeMode.ArriveBy
            ? best.OrderByDescending(option => option.LeaveBy)
            : best.OrderBy(option => option.ArriveAt);
        var result = ordered.Take(MaxOptions).ToList();
        if (watchRide is not null && result.All(option => option.RideId != watchRide) &&
            best.FirstOrDefault(option => option.RideId == watchRide) is { } watched)
        {
            result.Add(watched);
        }

        logger.LogDebug("Planned {Count} direct options from {Candidates} candidate departures.", result.Count, candidates.Count);
        return new TripPlan(result.Count == 0 ? TripPlanStatus.NoOptions : TripPlanStatus.Ok, result);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private async Task<Dictionary<string, List<Arrival>>> LoadLiveAsync(IReadOnlyList<NearbyStop> originStops, CancellationToken cancellationToken)
    {
        var live = new Dictionary<string, List<Arrival>>();
        foreach (var origin in originStops)
        {
            var result = await aggregator.GetArrivalsAsync(origin.Stop.Code, cancellationToken);
            live[origin.Stop.Code] = result.Arrivals.Where(arrival => arrival.IsLive).ToList();
        }

        return live;
    }

    /// <summary>Pairs a scheduled departure with the live arrival of the same line closest in time, using each live arrival once.</summary>
    private static Arrival? MatchLive(Dictionary<string, List<Arrival>> live, string stopCode, RideStop departure)
    {
        if (!live.TryGetValue(stopCode, out var arrivals))
        {
            return null;
        }

        var match = arrivals
            .Where(arrival => string.Equals(arrival.LineName, departure.Line, StringComparison.OrdinalIgnoreCase))
            .Select(arrival => (Arrival: arrival, Gap: (arrival.Eta - departure.Time).Duration()))
            .Where(candidate => candidate.Gap <= LiveMatchTolerance)
            .OrderBy(candidate => candidate.Gap)
            .Select(candidate => candidate.Arrival)
            .FirstOrDefault();
        if (match is not null)
        {
            arrivals.Remove(match);
        }

        return match;
    }
}

/// <summary>Route names look like "origin&lt;-&gt;destination-city-alternative".</summary>
public static class RouteEndpoints
{
    public static string? Origin(string? routeLongName)
    {
        if (string.IsNullOrWhiteSpace(routeLongName))
        {
            return null;
        }

        var separator = routeLongName.IndexOf("<->", StringComparison.Ordinal);
        if (separator < 0)
        {
            return null;
        }

        return StripCity(routeLongName[..separator]);
    }

    public static string? Destination(string? routeLongName) => StrideScheduleProvider.DestinationFromRouteName(routeLongName);

    private static string? StripCity(string text)
    {
        var lastDash = text.LastIndexOf('-');
        var name = (lastDash > 0 ? text[..lastDash] : text).Trim();
        return name.Length == 0 ? null : name;
    }
}
