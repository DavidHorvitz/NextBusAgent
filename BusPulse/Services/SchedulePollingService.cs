using System.IO;
using System.Net.Http;
using System.Text.Json;
using BusPulse.Configuration;
using BusPulse.Services.LiveData;
using BusPulse.Services.Stops;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services;

public sealed class SchedulePollingService(
    ILiveDataAggregator aggregator,
    AppConfig config,
    IStopCatalog catalog,
    ILogger<SchedulePollingService> logger) : BackgroundService
{
    private static readonly TimeSpan FarArrivalInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ApproachingArrivalInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NearArrivalInterval = TimeSpan.FromSeconds(20);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using var timer = new PeriodicTimer(GetPollingInterval(estimatedArrivalMinutes: null));
            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }

            await PollOnceAsync(stoppingToken);
        }
    }

    /// <summary>One polling pass. Does nothing when the start place, destination or line is not configured.</summary>
    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var startPlace = config.SavedPlaces.FirstOrDefault(place => place.Id == config.StartPlaceId);
        var destinationPlace = config.SavedPlaces.FirstOrDefault(place => place.Id == config.DestinationPlaceId);
        var lineName = config.LineName;
        if (startPlace is null || destinationPlace is null || string.IsNullOrWhiteSpace(lineName))
        {
            return;
        }

        await PollAsync(startPlace, lineName, cancellationToken);
    }

    private static TimeSpan GetPollingInterval(double? estimatedArrivalMinutes)
    {
        if (estimatedArrivalMinutes is null || estimatedArrivalMinutes > 15)
        {
            return FarArrivalInterval;
        }

        return estimatedArrivalMinutes <= 5
            ? NearArrivalInterval
            : ApproachingArrivalInterval;
    }

    /// <summary>A place with coordinates uses the stop nearest to them; otherwise its own stop code.</summary>
    private async Task<string?> ResolveStopCodeAsync(SavedPlace place, CancellationToken cancellationToken)
    {
        if (place.Latitude is null || place.Longitude is null)
        {
            return place.StopCode.Length > 0 ? place.StopCode : null;
        }

        var stops = await catalog.GetStopsAsync(cancellationToken);
        return PlacePoint.NearestStop(place, stops)?.Code;
    }

    private async Task PollAsync(SavedPlace startPlace, string lineName, CancellationToken cancellationToken)
    {
        try
        {
            var stopCode = await ResolveStopCodeAsync(startPlace, cancellationToken);
            if (stopCode is null)
            {
                logger.LogInformation("No stop could be resolved for place {PlaceName}; skipping this poll.", startPlace.Name);
                return;
            }

            var result = await aggregator.GetArrivalsAsync(stopCode, cancellationToken);
            var matches = result.Arrivals
                .Where(arrival => string.Equals(arrival.LineName, lineName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            logger.LogInformation(
                "Polled line {LineName} at place {PlaceName}: {LiveCount} live, {ScheduledCount} scheduled.",
                lineName,
                startPlace.Name,
                matches.Count(arrival => arrival.IsLive),
                matches.Count(arrival => !arrival.IsLive));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            logger.LogWarning(exception, "Polling failed; the next scheduled poll will retry.");
        }
    }
}
