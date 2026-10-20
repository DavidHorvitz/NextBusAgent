using BusPulse.Services.Stops;
using Microsoft.Extensions.Logging;
using Windows.Devices.Geolocation;

namespace BusPulse.Services.Location;

public enum LocationStatus
{
    Ok,
    PermissionDenied,
    Unavailable,
    OutsideCoverage
}

public sealed record LocationResult(LocationStatus Status, double Latitude = 0, double Longitude = 0, double AccuracyMeters = 0)
{
    public static LocationResult Failed(LocationStatus status) => new(status);
}

public interface ILocationService
{
    Task<LocationResult> GetCurrentAsync(CancellationToken cancellationToken = default);
}

/// <summary>Windows Location API with high accuracy. No IP-based fallback: that would send the user's IP to a third party.</summary>
public sealed class WindowsLocationService(ILogger<WindowsLocationService> logger) : ILocationService
{
    public async Task<LocationResult> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var access = await Geolocator.RequestAccessAsync().AsTask(cancellationToken);
            if (access != GeolocationAccessStatus.Allowed)
            {
                return LocationResult.Failed(LocationStatus.PermissionDenied);
            }

            var locator = new Geolocator { DesiredAccuracy = PositionAccuracy.High };
            var position = await locator
                .GetGeopositionAsync(TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(10))
                .AsTask(cancellationToken);
            var point = position.Coordinate.Point.Position;
            if (!Geo.IsInIsrael(point.Latitude, point.Longitude))
            {
                return LocationResult.Failed(LocationStatus.OutsideCoverage);
            }

            return new LocationResult(LocationStatus.Ok, point.Latitude, point.Longitude, position.Coordinate.Accuracy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return LocationResult.Failed(LocationStatus.PermissionDenied);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logger.LogWarning(exception, "Windows location lookup failed.");
            return LocationResult.Failed(LocationStatus.Unavailable);
        }
    }
}
