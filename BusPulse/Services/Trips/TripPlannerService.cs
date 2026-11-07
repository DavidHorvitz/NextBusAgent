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
    NoOptions,

    /// <summary>A date after today was asked for and the timetable source has no departures for it yet.</summary>
    NoScheduleForDate
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
        var originStops = await WithCurrentIdsAsync(
            Geo.FindNearby(stops.Where(stop => stop.Id > 0), request.OriginLatitude, request.OriginLongitude, OriginStopCount, MaxWalkMeters),
            cancellationToken);
        if (originStops.Count == 0)
        {
            return new TripPlan(TripPlanStatus.NoStopsNearOrigin, []);
        }

        var destinationStops = await WithCurrentIdsAsync(
            Geo.FindNearby(stops.Where(stop => stop.Id > 0), request.DestinationLatitude, request.DestinationLongitude, DestinationStopCount, MaxWalkMeters),
            cancellationToken);
        if (destinationStops.Count == 0)
        {
            return new TripPlan(TripPlanStatus.NoStopsNearDestination, []);
        }

        var target = request.TargetTime;
        var futureDate = request.Mode != TripTimeMode.Now && target is not null &&
            TripTime.TodayIn(target.Value, TripTime.IsraelZone) > TripTime.TodayIn(now, TripTime.IsraelZone);
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
        // Each Stride query can take many seconds, so the origin stops are fetched together. A query returns a limited
        // number of rows, so arrive-by asks for the latest departures first: those are the ones closest to the target.
        var latestFirst = request.Mode == TripTimeMode.ArriveBy && target is not null;
        var perStop = await Task.WhenAll(originStops.Select(async origin =>
            (Origin: origin, Departures: await schedule.GetStopDeparturesAsync(origin.Stop.Id, departuresFrom, to, cancellationToken, latestFirst))));
        foreach (var (origin, departures) in perStop)
        {
            candidates.AddRange(departures
                .Where(departure => string.IsNullOrWhiteSpace(request.PreferredLine) ||
                    string.Equals(departure.Line, request.PreferredLine.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(departure => (departure, origin)));
        }

        if (futureDate && perStop.All(stop => stop.Departures.Count == 0))
        {
            // The source (Stride) only holds timetables up to today: say so instead of claiming there are no trips.
            return new TripPlan(TripPlanStatus.NoScheduleForDate, []);
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

        // Live arrivals describe today's buses; a ride on a later date is never matched to them or labelled live.
        var live = !futureDate && (request.Mode == TripTimeMode.Now || watchRide is not null)
            ? await LoadLiveAsync(originStops, cancellationToken)
            : new Dictionary<string, List<Arrival>>();
        var liveMatches = MatchLive(live, candidates);

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
            var liveMatch = liveMatches.GetValueOrDefault((origin.Stop.Code, departure.RideId));
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

    private async Task<IReadOnlyList<NearbyStop>> WithCurrentIdsAsync(IReadOnlyList<NearbyStop> nearby, CancellationToken cancellationToken)
    {
        var current = await catalog.WithCurrentIdsAsync(nearby.Select(n => n.Stop).ToArray(), cancellationToken);
        return nearby
            .Select(n => current.FirstOrDefault(stop => stop.Code == n.Stop.Code) is { } stop ? n with { Stop = stop } : null)
            .OfType<NearbyStop>()
            .ToArray();
    }

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

    /// <summary>
    /// Pairs scheduled departures with live arrivals, each used once, per origin stop. When the live trip ids and the
    /// Stride journey refs come from the same GTFS feed (at least one is equal), only equal trips are paired. They often
    /// do not: each source names trips from its own feed version (seen 2026-11-03), so otherwise every live arrival goes
    /// to the departure of its line closest in scheduled time. Either way a ride that has already left cannot take the
    /// ETA of the next bus.
    /// </summary>
    private static Dictionary<(string StopCode, long RideId), Arrival> MatchLive(
        Dictionary<string, List<Arrival>> live,
        IEnumerable<(RideStop Departure, NearbyStop Origin)> candidates)
    {
        var matches = new Dictionary<(string StopCode, long RideId), Arrival>();
        foreach (var stop in candidates.GroupBy(c => c.Origin.Stop.Code))
        {
            if (!live.TryGetValue(stop.Key, out var arrivals) || arrivals.Count == 0)
            {
                continue;
            }

            var departures = stop.Select(c => c.Departure).DistinctBy(d => d.RideId).ToArray();
            var journeyRefs = departures.Select(d => d.JourneyRef).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var used = new HashSet<Arrival>(ReferenceEqualityComparer.Instance);
            if (arrivals.Any(arrival => arrival.TripRef is { } trip && journeyRefs.Contains(trip)))
            {
                foreach (var departure in departures)
                {
                    if (departure.JourneyRef is { } journey &&
                        arrivals.FirstOrDefault(arrival => !used.Contains(arrival) && string.Equals(arrival.TripRef, journey, StringComparison.Ordinal)) is { } same)
                    {
                        matches[(stop.Key, departure.RideId)] = same;
                        used.Add(same);
                    }
                }

                continue;
            }

            var pairs = arrivals
                .SelectMany(arrival => departures
                    .Where(departure => string.Equals(arrival.LineName, departure.Line, StringComparison.OrdinalIgnoreCase))
                    .Select(departure => (Arrival: arrival, Departure: departure, Gap: (arrival.Eta - departure.Time).Duration())))
                .Where(pair => pair.Gap <= LiveMatchTolerance)
                .OrderBy(pair => pair.Gap);
            foreach (var (arrival, departure, _) in pairs)
            {
                if (!used.Contains(arrival) && matches.TryAdd((stop.Key, departure.RideId), arrival))
                {
                    used.Add(arrival);
                }
            }
        }

        return matches;
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
