using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using BusPulse.Infrastructure;
using BusPulse.Services.Stops;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Geocoding;

/// <summary>
/// Address search against Nominatim (OpenStreetMap). Follows the OSMF usage policy: at most one request per second
/// for the whole app, an identifying User-Agent, results cached so a query is never sent twice, no search-as-you-type
/// (the caller decides when to search), the server address comes from the configuration, and the attribution is exposed
/// as <see cref="Attribution"/>. The typed text is never logged.
/// </summary>
public sealed partial class NominatimGeocoder(
    IHttpClientFactory httpClientFactory,
    IClock clock,
    ISettingsService settings,
    ILogger<NominatimGeocoder> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    GeocodeDiskCache? diskCache = null) : IGeocoder
{
    public const string ClientName = "Geocoder";
    public const string Attribution = "© OpenStreetMap contributors";
    public const string UserAgent = "BusPulse/0.1 (+https://github.com/DavidHorvitz/NextBusAgent)";

    public const int MinQueryLength = 3;
    public const int MaxQueryLength = 120;
    public const int MaxResults = 5;
    public const int MaxCacheEntries = 100;

    private const int MaxResponseBytes = 256 * 1024;
    private const int MaxStringLength = 128;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromSeconds(1);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset StoredAt, GeocodeResult Result)> _cache = new(StringComparer.Ordinal);
    private readonly ServiceCircuitBreaker _breaker = new(clock);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    [GeneratedRegex(@"^(?:רחוב|רח'|רח׳|רח|street|st\.)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingStreetWord();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Trims, collapses spaces and drops a leading street word ("רחוב הרצל 10" becomes "הרצל 10"; Nominatim returns nothing for the long form).</summary>
    public static string NormalizeQuery(string? query)
    {
        var text = Whitespace().Replace((query ?? string.Empty).Trim(), " ");
        return LeadingStreetWord().Replace(text, string.Empty, 1).Trim();
    }

    public async Task<GeocodeResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (!settings.Config.AddressSearchEnabled)
        {
            return GeocodeResult.Of(GeocodeStatus.Disabled);
        }

        var normalized = NormalizeQuery(query);
        if (normalized.Length < MinQueryLength)
        {
            return GeocodeResult.Of(GeocodeStatus.TooShort);
        }

        if (normalized.Length > MaxQueryLength || normalized.Any(char.IsControl))
        {
            return GeocodeResult.Of(GeocodeStatus.NoResults);
        }

        if (!TryBuildBaseUri(out var baseUri))
        {
            return GeocodeResult.Of(GeocodeStatus.Unavailable);
        }

        var key = baseUri.AbsoluteUri + "|" + normalized.ToLowerInvariant();
        if (TryGetCached(key, out var cached))
        {
            return cached;
        }

        if (_breaker.IsOpen)
        {
            return GeocodeResult.Of(GeocodeStatus.RateLimited);
        }

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            // A concurrent identical search may have filled the cache while this one waited.
            if (TryGetCached(key, out cached))
            {
                return cached;
            }

            if (_breaker.IsOpen)
            {
                return GeocodeResult.Of(GeocodeStatus.RateLimited);
            }

            var wait = _lastRequestAt + MinRequestSpacing - clock.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait, cancellationToken);
            }

            _lastRequestAt = clock.UtcNow;
            var result = await FetchAsync(baseUri, normalized, cancellationToken);
            if (result.Status is GeocodeStatus.Ok or GeocodeStatus.NoResults)
            {
                Store(key, result);
            }

            return result;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public IReadOnlyList<GeocodedPlace> Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            MaxDepth = 16,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Geocoder response must be an array.");
        }

        var places = new List<GeocodedPlace>();
        foreach (var item in document.RootElement.EnumerateArray().Take(MaxResults))
        {
            if (!TryParsePlace(item, out var place))
            {
                continue;
            }

            // Several OSM objects at one address (building, shop, entrance) would otherwise repeat the same label.
            if (places.Any(existing => existing.Label == place.Label &&
                                       Geo.DistanceMeters(existing.Latitude, existing.Longitude, place.Latitude, place.Longitude) < 150))
            {
                continue;
            }

            places.Add(place);
        }

        return places;
    }

    private async Task<GeocodeResult> FetchAsync(Uri baseUri, string normalized, CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(baseUri, "search?q=" + Uri.EscapeDataString(normalized) +
                "&format=jsonv2&addressdetails=1&countrycodes=il&limit=" + MaxResults.ToString(CultureInfo.InvariantCulture) +
                "&accept-language=he");
            var client = httpClientFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (ServiceCircuitBreaker.ShouldTrip(response.StatusCode))
            {
                _breaker.Trip();
                logger.LogWarning("Address service answered {Status}; pausing it for ten minutes.", (int)response.StatusCode);
                return GeocodeResult.Of(GeocodeStatus.RateLimited);
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                throw new JsonException("Geocoder response exceeds the size limit.");
            }

            var bytes = await ReadCappedAsync(response, cancellationToken);
            var places = Parse(bytes);
            return places.Count == 0
                ? GeocodeResult.Of(GeocodeStatus.NoResults)
                : new GeocodeResult(GeocodeStatus.Ok, places);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
        {
            // The query text is deliberately not logged.
            logger.LogWarning(exception, "Address search failed ({Kind}).", exception.GetType().Name);
            return GeocodeResult.Of(GeocodeStatus.Unavailable);
        }
    }

    private bool TryBuildBaseUri(out Uri baseUri)
    {
        var text = settings.Config.GeocoderBaseUrl;
        if (text is { Length: <= 200 } && Uri.TryCreate(text, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps)
        {
            baseUri = text.EndsWith('/') ? parsed : new Uri(text + "/", UriKind.Absolute);
            return true;
        }

        baseUri = null!;
        return false;
    }

    private bool TryGetCached(string key, out GeocodeResult result)
    {
        if (TryGetMemory(key, out result))
        {
            return true;
        }

        if (diskCache is not null && diskCache.TryGet(key, out result))
        {
            StoreMemory(key, result);
            return true;
        }

        return false;
    }

    private bool TryGetMemory(string key, out GeocodeResult result)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var entry))
            {
                if (clock.UtcNow - entry.StoredAt < CacheDuration)
                {
                    result = entry.Result;
                    return true;
                }

                _cache.Remove(key);
            }
        }

        result = null!;
        return false;
    }

    private void Store(string key, GeocodeResult result)
    {
        StoreMemory(key, result);
        diskCache?.Store(key, result);
    }

    private void StoreMemory(string key, GeocodeResult result)
    {
        lock (_cacheLock)
        {
            var now = clock.UtcNow;
            foreach (var expired in _cache.Where(pair => now - pair.Value.StoredAt >= CacheDuration).Select(pair => pair.Key).ToList())
            {
                _cache.Remove(expired);
            }

            while (_cache.Count >= MaxCacheEntries && !_cache.ContainsKey(key))
            {
                _cache.Remove(_cache.MinBy(pair => pair.Value.StoredAt).Key);
            }

            _cache[key] = (now, result);
        }
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new JsonException("Geocoder response exceeds the size limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static bool TryParsePlace(JsonElement item, out GeocodedPlace place)
    {
        place = null!;
        if (item.ValueKind != JsonValueKind.Object ||
            !TryGetCoordinate(item, "lat", out var latitude) ||
            !TryGetCoordinate(item, "lon", out var longitude) ||
            !Geo.IsInIsrael(latitude, longitude))
        {
            return false;
        }

        var name = GetString(item, "name");
        string? road = null, houseNumber = null, locality = null;
        if (item.TryGetProperty("address", out var address) && address.ValueKind == JsonValueKind.Object)
        {
            road = GetString(address, "road");
            houseNumber = GetString(address, "house_number");
            locality = GetString(address, "city") ?? GetString(address, "town") ?? GetString(address, "village") ??
                       GetString(address, "municipality") ?? GetString(address, "hamlet") ?? GetString(address, "suburb");
        }

        // A name that is only digits is the house number repeated, not a place name.
        if (name is not null && name.All(char.IsDigit))
        {
            name = null;
        }

        // A named place without a house number (the Western Wall, a station) reads better by name than by the road beside it.
        string? main;
        if (name is not null && houseNumber is null && name != road)
        {
            main = name;
        }
        else if (road is not null)
        {
            main = houseNumber is null ? road : road + " " + houseNumber;
        }
        else
        {
            main = name;
        }

        if (main is null && locality is null)
        {
            return false;
        }

        var label = main is null ? locality! : locality is null || locality == main ? main : main + ", " + locality;
        place = new GeocodedPlace(label, latitude, longitude);
        return true;
    }

    private static bool TryGetCoordinate(JsonElement item, string name, out double value)
    {
        value = 0;
        if (!item.TryGetProperty(name, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                                    double.IsFinite(value),
            JsonValueKind.Number => element.TryGetDouble(out value) && double.IsFinite(value),
            _ => false
        };
    }

    /// <summary>A trimmed string without control characters, or null when absent, not a string, empty or over the length cap.</summary>
    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString()?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxStringLength || text.Any(char.IsControl))
        {
            return null;
        }

        return text;
    }
}
