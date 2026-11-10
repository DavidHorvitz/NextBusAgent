using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using BusPulse.Services.Geocoding;
using BusPulse.Services.Stops;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Trips;

/// <summary>A walk between two points. <see cref="IsEstimate"/> is true when it is straight-line distance times a detour factor.</summary>
public sealed record WalkRoute(double Meters, TimeSpan Duration, bool IsEstimate);

public interface IWalkingRouter
{
    /// <summary>Never throws for a service failure: it falls back to an estimate. Cancellation still throws.</summary>
    Task<WalkRoute> GetWalkAsync(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude, CancellationToken cancellationToken = default);
}

/// <summary>
/// Pedestrian routing over the OSRM "foot" profile hosted by FOSSGIS (routing.openstreetmap.de). Its usage policy allows
/// at most one request per second, asks for an identifying User-Agent and is meant for light personal use. So: requests are
/// spaced at 1 s, answers are cached for 24 h (coordinates rounded to about 11 m), a call is cut off after 4 s, and a 429 or
/// 403 stops all calls for ten minutes. Any failure gives <see cref="Walking.Fallback"/> (straight line times 1.3).
/// </summary>
public sealed class OsrmWalkingRouter(
    IHttpClientFactory httpClientFactory,
    IClock clock,
    ILogger<OsrmWalkingRouter> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IWalkingRouter
{
    public const string ClientName = "WalkingRouter";
    public static readonly Uri DefaultBaseAddress = new("https://routing.openstreetmap.de/routed-foot/");
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(4);
    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxCacheEntries = 400;
    private const double MaxPlausibleFactor = 4;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(1);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ServiceCircuitBreaker _breaker = new(clock);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset StoredAt, WalkRoute Route)> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    /// <summary>True while calls are paused after a 429 or 403.</summary>
    public bool IsPaused => _breaker.IsOpen;

    public async Task<WalkRoute> GetWalkAsync(
        double fromLatitude, double fromLongitude, double toLatitude, double toLongitude, CancellationToken cancellationToken = default)
    {
        var straight = Geo.DistanceMeters(fromLatitude, fromLongitude, toLatitude, toLongitude);
        var fallback = Walking.Fallback(straight);
        if (!Geo.IsInIsrael(fromLatitude, fromLongitude) || !Geo.IsInIsrael(toLatitude, toLongitude))
        {
            return fallback;
        }

        if (straight < 15)
        {
            return new WalkRoute(straight, Walking.TimeFor(straight), IsEstimate: false);
        }

        var key = string.Create(CultureInfo.InvariantCulture, $"{fromLatitude:F4},{fromLongitude:F4};{toLatitude:F4},{toLongitude:F4}");
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var hit) && clock.UtcNow - hit.StoredAt < CacheDuration)
            {
                return hit.Route;
            }
        }

        if (_breaker.IsOpen)
        {
            return fallback;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_breaker.IsOpen)
            {
                return fallback;
            }

            var wait = MinSpacing - (clock.UtcNow - _lastRequestAt);
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait, cancellationToken);
            }

            _lastRequestAt = clock.UtcNow;
            var route = await FetchAsync(fromLatitude, fromLongitude, toLatitude, toLongitude, straight, cancellationToken);
            if (route is null)
            {
                return fallback;
            }

            lock (_cacheLock)
            {
                if (_cache.Count >= MaxCacheEntries)
                {
                    _cache.Remove(_cache.OrderBy(pair => pair.Value.StoredAt).First().Key);
                }

                _cache[key] = (clock.UtcNow, route);
            }

            return route;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WalkRoute?> FetchAsync(
        double fromLatitude, double fromLongitude, double toLatitude, double toLongitude, double straight, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);
        try
        {
            var c = CultureInfo.InvariantCulture;
            var uri = new Uri(DefaultBaseAddress, string.Create(c, $"route/v1/foot/{fromLongitude:F6},{fromLatitude:F6};{toLongitude:F6},{toLatitude:F6}?overview=false"));
            var client = httpClientFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd(NominatimGeocoder.UserAgent);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (ServiceCircuitBreaker.ShouldTrip(response.StatusCode))
            {
                _breaker.Trip();
                logger.LogWarning("Walking route service answered {Status}; pausing it for ten minutes.", (int)response.StatusCode);
                return null;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxResponseBytes)
                {
                    return null;
                }
            }

            return Parse(buffer.ToArray(), straight);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            logger.LogDebug(exception, "Walking route unavailable; using a straight-line estimate.");
            return null;
        }
    }

    /// <summary>Reads the first route's distance. Null when the answer is not usable or implausible for the straight-line distance.</summary>
    internal static WalkRoute? Parse(byte[] json, double straightMeters)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String || code.GetString() != "Ok" ||
            !root.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0)
        {
            return null;
        }

        var first = routes[0];
        if (first.ValueKind != JsonValueKind.Object ||
            !first.TryGetProperty("distance", out var distance) || distance.ValueKind != JsonValueKind.Number ||
            !distance.TryGetDouble(out var meters) || double.IsNaN(meters) || meters < 0)
        {
            return null;
        }

        // A walk can be longer than the straight line, never shorter, and never absurdly longer.
        if (meters < straightMeters * 0.9 - 5 || meters > Math.Max(straightMeters * MaxPlausibleFactor, 200))
        {
            return null;
        }

        return new WalkRoute(meters, Walking.TimeFor(meters), IsEstimate: false);
    }
}
