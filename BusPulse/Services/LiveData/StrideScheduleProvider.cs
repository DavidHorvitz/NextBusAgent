using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.LiveData;

/// <summary>Scheduled (never live) arrivals from the Stride GTFS static tables.</summary>
public sealed partial class StrideScheduleProvider(
    IHttpClientFactory httpClientFactory,
    IClock clock,
    ILogger<StrideScheduleProvider> logger,
    BusPulse.Services.Stops.IStopCatalog? catalog = null) : IScheduleProvider
{
    private const string BaseAddress = "https://open-bus-stride-api.hasadna.org.il/";
    private const int MaxResponseBytes = 1_048_576;
    private const int MaxStringLength = 128;
    private const int MaxRows = 100;
    private static readonly TimeSpan LookBehind = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LookAhead = TimeSpan.FromMinutes(90);

    private readonly ConcurrentDictionary<string, long> _stopIds = new();

    public async Task<ProviderResult> GetScheduleAsync(string stopCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stopCode) || stopCode.Length > 12 || !stopCode.All(char.IsAsciiDigit))
        {
            return ProviderResult.Failed(ProviderStatus.Unavailable, "Invalid stop code.");
        }

        try
        {
            var now = clock.UtcNow;
            var stopId = await ResolveStopIdAsync(stopCode, now, cancellationToken);
            if (stopId is null)
            {
                return new ProviderResult(ProviderStatus.NoData, []);
            }

            var from = (now - LookBehind).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "%2B00:00";
            var to = (now + LookAhead).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "%2B00:00";
            var uri = $"{BaseAddress}gtfs_ride_stops/list?gtfs_stop_ids={stopId}&arrival_time_from={from}&arrival_time_to={to}" +
                $"&order_by=arrival_time%20asc&limit={MaxRows}";
            var arrivals = ParseRideStops(stopCode, await GetBytesAsync(uri, cancellationToken), now);
            return arrivals.Count == 0
                ? new ProviderResult(ProviderStatus.NoData, [])
                : new ProviderResult(ProviderStatus.Ok, arrivals);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Stride schedule request failed for stop {StopCode}.", stopCode);
            return ProviderResult.Failed(ProviderStatus.Unavailable, exception.GetType().Name);
        }
    }

    public static long? ParseStopId(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), StrictOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Stride stops response must be an array.");
        }

        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("id", out var id) &&
                id.ValueKind == JsonValueKind.Number &&
                id.TryGetInt64(out var value))
            {
                return value;
            }
        }

        return null;
    }

    public static IReadOnlyList<Arrival> ParseRideStops(string stopCode, ReadOnlySpan<byte> json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json.ToArray(), StrictOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Stride ride-stops response must be an array.");
        }

        var arrivals = new List<Arrival>();
        foreach (var row in document.RootElement.EnumerateArray().Take(MaxRows))
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !TryGetString(row, "gtfs_route__route_short_name", out var line) ||
                !row.TryGetProperty("arrival_time", out var arrivalProperty) ||
                arrivalProperty.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(arrivalProperty.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var eta))
            {
                continue;
            }

            TryGetString(row, "gtfs_route__agency_name", out var agency);
            TryGetString(row, "gtfs_route__route_long_name", out var longName);
            arrivals.Add(new Arrival(
                stopCode,
                line,
                string.IsNullOrEmpty(agency) ? null : agency,
                DestinationFromRouteName(longName),
                eta,
                IsLive: false,
                ArrivalSource.Schedule,
                now));
        }

        return arrivals.OrderBy(arrival => arrival.Eta).ToArray();
    }

    /// <summary>Route names look like "origin&lt;-&gt;destination-city-alternative"; keep only the destination.</summary>
    public static string? DestinationFromRouteName(string? routeLongName)
    {
        if (string.IsNullOrWhiteSpace(routeLongName))
        {
            return null;
        }

        var separator = routeLongName.IndexOf("<->", StringComparison.Ordinal);
        var destination = separator >= 0 ? routeLongName[(separator + 3)..] : routeLongName;
        destination = RouteSuffix().Replace(destination, string.Empty);
        destination = destination.Trim();
        return destination.Length == 0 ? null : destination;
    }

    private static readonly JsonDocumentOptions StrictOptions = new()
    {
        MaxDepth = 32,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    [GeneratedRegex(@"-[^-]+-[^-]+$")]
    private static partial Regex RouteSuffix();

    private async Task<long?> ResolveStopIdAsync(string stopCode, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var date = IsraelDate(now);
        var cacheKey = $"{stopCode}:{date}";
        if (_stopIds.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        // The stop list already holds the id; this saves one slow Stride round trip per stop.
        if (catalog?.Loaded?.FirstOrDefault(stop => stop.Code == stopCode && stop.Id > 0) is { } known)
        {
            _stopIds[cacheKey] = known.Id;
            return known.Id;
        }

        var uri = $"{BaseAddress}gtfs_stops/list?code={stopCode}&date_from={date}&date_to={date}&limit=2";
        var stopId = ParseStopId(await GetBytesAsync(uri, cancellationToken));
        if (stopId is not null)
        {
            _stopIds[cacheKey] = stopId.Value;
        }

        return stopId;
    }

    private async Task<byte[]> GetBytesAsync(string uri, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("TransitApi");
        using var response = await client.GetAsync(new Uri(uri, UriKind.Absolute), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new JsonException("Stride response exceeds the size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new JsonException("Stride response exceeds the size limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static string IsraelDate(DateTimeOffset now)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
            return TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (TimeZoneNotFoundException)
        {
            return now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
    }

    private static bool TryGetString(JsonElement row, string name, out string value)
    {
        value = string.Empty;
        if (!row.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text.Length <= MaxStringLength ? text : text[..MaxStringLength];
        return true;
    }
}
