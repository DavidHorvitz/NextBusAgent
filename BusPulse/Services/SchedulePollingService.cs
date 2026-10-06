using System.IO;
using System.Net.Http;
using System.Text.Json;
using BusPulse.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services;

public sealed class SchedulePollingService(
    OpenBusService openBusService,
    AppConfig config,
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

            var startPlace = config.SavedPlaces.FirstOrDefault(place => place.Id == config.StartPlaceId);
            var destinationPlace = config.SavedPlaces.FirstOrDefault(place => place.Id == config.DestinationPlaceId);
            var lineName = config.LineName;
            if (startPlace is null || destinationPlace is null || string.IsNullOrWhiteSpace(lineName))
            {
                continue;
            }

            await PollOpenBusAsync(startPlace, lineName, stoppingToken);
        }
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

    private async Task PollOpenBusAsync(SavedPlace startPlace, string lineName, CancellationToken cancellationToken)
    {
        try
        {
            var arrivals = await openBusService.GetBusArrivalsAsync(startPlace.StopCode, lineName, cancellationToken);
            logger.LogInformation(
                "Received {ArrivalCount} OpenBus records for line {LineName} at place {PlaceName}.",
                arrivals.Count,
                lineName,
                startPlace.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            logger.LogWarning(exception, "OpenBus polling failed; the next scheduled poll will retry.");
        }
    }
}