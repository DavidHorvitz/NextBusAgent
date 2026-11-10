using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using BusPulse.Infrastructure;
using BusPulse.Services.Secrets;
using BusPulse.Services.Stops;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Geocoding;

/// <summary>
/// Google Places API (New): Autocomplete for suggestions and Place Details for the coordinates of the chosen one.
/// Disabled until a key exists (<see cref="SecretNames.GooglePlacesApiKey"/>). One typing session (several Autocomplete
/// calls followed by one Details call) shares a session token, so it is billed as a single session. A local daily cap
/// of sessions (<c>GooglePlacesDailySessionCap</c>, default 200) stops all calls when reached. The key and the typed
/// text are never logged. After HTTP 429 or 403 the service is left alone for ten minutes.
/// </summary>
public sealed class GooglePlacesGeocoder(
    IHttpClientFactory httpClientFactory,
    IClock clock,
    ISettingsService settings,
    ISecretProvider secrets,
    PlacesUsageStore usage,
    ILogger<GooglePlacesGeocoder> logger) : IGeocoder
{
    public const string ClientName = "GooglePlaces";
    public const string AutocompleteUrl = "https://places.googleapis.com/v1/places:autocomplete";
    public const string DetailsUrl = "https://places.googleapis.com/v1/places/";
    public const int MinQueryLength = 3;
    public const int MaxQueryLength = 120;
    public const int MaxResults = 5;

    private const int MaxResponseBytes = 128 * 1024;
    private const int MaxStringLength = 160;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan KeyRecheck = TimeSpan.FromMinutes(10);

    private readonly ServiceCircuitBreaker _breaker = new(clock);
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private readonly object _sessionLock = new();
    private string? _apiKey;
    private DateTimeOffset _keyCheckedAt = DateTimeOffset.MinValue;
    private string? _sessionToken;
    private DateTimeOffset _sessionStartedAt;

    /// <summary>True once a key was found. The key is read in the background; until then the provider reports false.</summary>
    public bool HasKey => _apiKey is not null;

    public bool SupportsAutocomplete => HasKey && settings.Config.AddressSearchEnabled;

    /// <summary>Reads the key from the secret provider (Infisical, then the environment); retried at most every ten minutes.</summary>
    public async Task EnsureKeyAsync(CancellationToken cancellationToken = default)
    {
        if (_apiKey is not null || clock.UtcNow - _keyCheckedAt < KeyRecheck)
        {
            return;
        }

        await _keyGate.WaitAsync(cancellationToken);
        try
        {
            if (_apiKey is not null || clock.UtcNow - _keyCheckedAt < KeyRecheck)
            {
                return;
            }

            _keyCheckedAt = clock.UtcNow;
            var key = (await secrets.GetAsync(SecretNames.GooglePlacesApiKey, cancellationToken))?.Trim();
            _apiKey = string.IsNullOrEmpty(key) || key.Length > 256 || key.Any(char.IsControl) ? null : key;
        }
        finally
        {
            _keyGate.Release();
        }
    }

    public Task<GeocodeResult> SuggestAsync(string query, CancellationToken cancellationToken = default) => SearchAsync(query, cancellationToken);

    public async Task<GeocodeResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (!settings.Config.AddressSearchEnabled)
        {
            return GeocodeResult.Of(GeocodeStatus.Disabled);
        }

        await EnsureKeyAsync(cancellationToken);
        if (_apiKey is null)
        {
            return GeocodeResult.Of(GeocodeStatus.Disabled);
        }

        var text = NominatimGeocoder.NormalizeQuery(query);
        if (text.Length < MinQueryLength)
        {
            return GeocodeResult.Of(GeocodeStatus.TooShort);
        }

        if (text.Length > MaxQueryLength || text.Any(char.IsControl))
        {
            return GeocodeResult.Of(GeocodeStatus.NoResults);
        }

        if (_breaker.IsOpen)
        {
            return GeocodeResult.Of(GeocodeStatus.RateLimited);
        }

        var (token, isNew) = CurrentSession();
        if (isNew && !usage.TryStartSession(settings.Config.GooglePlacesDailySessionCap))
        {
            EndSession();
            return GeocodeResult.Of(GeocodeStatus.DailyCapReached);
        }

        try
        {
            var body = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["input"] = text,
                ["languageCode"] = "he",
                ["regionCode"] = "il",
                ["sessionToken"] = token
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, AutocompleteUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", _apiKey);
            request.Headers.UserAgent.ParseAdd(NominatimGeocoder.UserAgent);
            var bytes = await SendAsync(request, cancellationToken);
            if (bytes is null)
            {
                return GeocodeResult.Of(_breaker.IsOpen ? GeocodeStatus.RateLimited : GeocodeStatus.Unavailable);
            }

            var places = ParseAutocomplete(bytes);
            return places.Count == 0 ? GeocodeResult.Of(GeocodeStatus.NoResults) : new GeocodeResult(GeocodeStatus.Ok, places);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Place suggestions failed ({Kind}).", exception.GetType().Name);
            return GeocodeResult.Of(GeocodeStatus.Unavailable);
        }
    }

    public async Task<GeocodedPlace?> ResolveAsync(GeocodedPlace place, CancellationToken cancellationToken = default)
    {
        if (!place.IsUnresolved)
        {
            return place;
        }

        await EnsureKeyAsync(cancellationToken);
        if (_apiKey is null || _breaker.IsOpen || place.PlaceId!.Length > 256 || place.PlaceId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
        {
            return null;
        }

        string? token;
        lock (_sessionLock)
        {
            token = _sessionToken;
        }

        try
        {
            var uri = DetailsUrl + place.PlaceId + "?languageCode=he&regionCode=il" +
                      (token is null ? string.Empty : "&sessionToken=" + Uri.EscapeDataString(token));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", _apiKey);
            request.Headers.TryAddWithoutValidation("X-Goog-FieldMask", "location");
            request.Headers.UserAgent.ParseAdd(NominatimGeocoder.UserAgent);
            var bytes = await SendAsync(request, cancellationToken);
            // The details call closes the billing session whatever it returned.
            EndSession();
            return bytes is null ? null : ParseDetails(place, bytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Place details failed ({Kind}).", exception.GetType().Name);
            return null;
        }
    }

    /// <summary>The coordinates from a Place Details response, or null when they are missing or outside Israel.</summary>
    public static GeocodedPlace? ParseDetails(GeocodedPlace suggestion, ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), Options);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.Object ||
            !TryGetNumber(location, "latitude", out var latitude) || !TryGetNumber(location, "longitude", out var longitude) ||
            !Geo.IsInIsrael(latitude, longitude))
        {
            return null;
        }

        return suggestion with { Latitude = latitude, Longitude = longitude };
    }

    public static IReadOnlyList<GeocodedPlace> ParseAutocomplete(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), Options);
        var places = new List<GeocodedPlace>();
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Autocomplete response must be an object.");
        }

        // An empty result is "{}" with no suggestions member.
        if (!document.RootElement.TryGetProperty("suggestions", out var suggestions))
        {
            return places;
        }

        if (suggestions.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Autocomplete suggestions must be an array.");
        }

        foreach (var item in suggestions.EnumerateArray())
        {
            if (places.Count >= MaxResults)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("placePrediction", out var prediction) ||
                prediction.ValueKind != JsonValueKind.Object ||
                GetString(prediction, "placeId") is not { } placeId ||
                !prediction.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.Object ||
                GetString(textElement, "text") is not { } label ||
                placeId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            {
                continue;
            }

            places.Add(new GeocodedPlace(label, 0, 0, placeId));
        }

        return places;
    }

    private static readonly JsonDocumentOptions Options = new()
    {
        MaxDepth = 16,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private async Task<byte[]?> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (ServiceCircuitBreaker.ShouldTrip(response.StatusCode))
        {
            _breaker.Trip();
            logger.LogWarning("Places API answered {Status}; pausing it for ten minutes.", (int)response.StatusCode);
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new JsonException("Places response exceeds the size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new JsonException("Places response exceeds the size limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private (string Token, bool IsNew) CurrentSession()
    {
        lock (_sessionLock)
        {
            var now = clock.UtcNow;
            if (_sessionToken is not null && now - _sessionStartedAt < SessionLifetime)
            {
                return (_sessionToken, false);
            }

            _sessionToken = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            _sessionStartedAt = now;
            return (_sessionToken, true);
        }
    }

    private void EndSession()
    {
        lock (_sessionLock)
        {
            _sessionToken = null;
        }
    }

    private static bool TryGetNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString()?.Trim();
        return string.IsNullOrEmpty(text) || text.Length > MaxStringLength || text.Any(char.IsControl) ? null : text;
    }
}
