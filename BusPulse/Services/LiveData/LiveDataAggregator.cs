using Microsoft.Extensions.Logging;

namespace BusPulse.Services.LiveData;

public sealed record AggregatedArrivals(
    IReadOnlyList<Arrival> Arrivals,
    IReadOnlyDictionary<ArrivalSource, ProviderStatus> ProviderStatuses);

public interface ILiveDataAggregator
{
    Task<AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// Merges live providers (priority = <see cref="ArrivalSource"/> order) with the schedule. Per line the provider
/// with the freshest valid data wins; lines without live data fall back to scheduled times marked not live.
/// </summary>
public sealed class LiveDataAggregator(
    IEnumerable<ILiveArrivalsProvider> providers,
    IScheduleProvider? scheduleProvider,
    IClock clock,
    ILogger<LiveDataAggregator> logger,
    BusPulse.Services.Trips.Gtfs.IRouteColors? routeColors = null) : ILiveDataAggregator
{
    private static readonly TimeSpan MaxLiveAge = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PastTolerance = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxFutureEta = TimeSpan.FromHours(6);

    private readonly ILiveArrivalsProvider[] _providers = providers.OrderBy(provider => provider.Source).ToArray();

    public async Task<AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        var statuses = new Dictionary<ArrivalSource, ProviderStatus>();
        var liveCandidates = new List<Arrival>();

        foreach (var provider in _providers)
        {
            var result = await QueryAsync(provider.Source, () => provider.GetArrivalsAsync(stopCode, cancellationToken), cancellationToken);
            statuses[provider.Source] = result.Status;
            liveCandidates.AddRange(result.Arrivals.Where(arrival => IsValidLive(arrival, now, provider.Source)));
        }

        var live = PickFreshestPerLine(liveCandidates);
        var arrivals = new List<Arrival>(live);

        if (scheduleProvider is not null)
        {
            var schedule = await QueryAsync(ArrivalSource.Schedule, () => scheduleProvider.GetScheduleAsync(stopCode, cancellationToken), cancellationToken);
            statuses[ArrivalSource.Schedule] = schedule.Status;
            var liveLines = live.Select(arrival => arrival.LineName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            arrivals.AddRange(schedule.Arrivals
                .Where(arrival => !liveLines.Contains(arrival.LineName) && IsValidTime(arrival, now))
                .Select(arrival => arrival with { IsLive = false, Source = ArrivalSource.Schedule }));
        }

        logger.LogDebug(
            "Aggregated {Live} live and {Scheduled} scheduled arrivals for stop {StopCode}.",
            live.Count,
            arrivals.Count - live.Count,
            stopCode);
        return new AggregatedArrivals(
            arrivals
                .Select(arrival => arrival.RouteColor is null && routeColors?.Find(arrival.LineName, arrival.OperatorName) is { } color
                    ? arrival with { RouteColor = color }
                    : arrival)
                .OrderBy(arrival => arrival.Eta)
                .ToArray(),
            statuses);
    }

    private static List<Arrival> PickFreshestPerLine(List<Arrival> candidates)
    {
        var picked = new List<Arrival>();
        foreach (var line in candidates.GroupBy(arrival => arrival.LineName, StringComparer.OrdinalIgnoreCase))
        {
            var winner = line
                .GroupBy(arrival => arrival.Source)
                .OrderByDescending(group => group.Max(arrival => arrival.ObservedAt))
                .ThenBy(group => group.Key)
                .First();
            picked.AddRange(winner);
        }

        return picked;
    }

    private bool IsValidLive(Arrival arrival, DateTimeOffset now, ArrivalSource source)
    {
        if (!arrival.IsLive)
        {
            logger.LogDebug("{Source} arrival for line {Line} dropped: not marked live.", source, arrival.LineName);
            return false;
        }

        if (arrival.ObservedAt > now + ClockSkewTolerance || now - arrival.ObservedAt > MaxLiveAge)
        {
            logger.LogDebug("{Source} arrival for line {Line} dropped: stale or future observation time.", source, arrival.LineName);
            return false;
        }

        if (!IsValidTime(arrival, now))
        {
            logger.LogDebug("{Source} arrival for line {Line} dropped: ETA outside the valid window.", source, arrival.LineName);
            return false;
        }

        return true;
    }

    private static bool IsValidTime(Arrival arrival, DateTimeOffset now) =>
        arrival.Eta >= now - PastTolerance && arrival.Eta <= now + MaxFutureEta;

    private async Task<ProviderResult> QueryAsync(ArrivalSource source, Func<Task<ProviderResult>> query, CancellationToken cancellationToken)
    {
        try
        {
            return await query();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "{Source} provider threw; treating it as unavailable.", source);
            return ProviderResult.Failed(ProviderStatus.Unavailable, exception.GetType().Name);
        }
    }
}
