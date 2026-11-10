using System.Net;

namespace BusPulse.Services.Geocoding;

/// <summary>After HTTP 429 or 403 a service is left alone for a fixed time so the owner's IP or key is never blocked.</summary>
public sealed class ServiceCircuitBreaker(IClock clock, TimeSpan? pause = null)
{
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(10);

    private readonly TimeSpan _pause = pause ?? DefaultPause;
    private readonly object _gate = new();
    private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return clock.UtcNow < _openUntil;
            }
        }
    }

    public void Trip()
    {
        lock (_gate)
        {
            _openUntil = clock.UtcNow + _pause;
        }
    }

    public static bool ShouldTrip(HttpStatusCode status) => status is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden;
}
