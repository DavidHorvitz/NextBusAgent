using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Trips;

/// <summary>One scheduled stop of one ride (a single trip of a line).</summary>
public sealed record RideStop(
    long RideId,
    long StopId,
    int Sequence,
    DateTimeOffset Time,
    string Line,
    string? Agency,
    string? RouteLongName,
    string? StopName = null,
    string? StopCode = null);

public interface IRideScheduleSource
{
    /// <summary>Scheduled stops of any ride at one stop within a time window.</summary>
    Task<IReadOnlyList<RideStop>> GetStopDeparturesAsync(
        long stopId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    /// <summary>Every stop of one ride in calling order, within a time window around the ride.</summary>
    Task<IReadOnlyList<RideStop>> GetRideStopsAsync(
        long rideId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    /// <summary>Where the given rides call at the given stops (used to find direct connections).</summary>
    Task<IReadOnlyList<RideStop>> GetRideStopsAtAsync(
        IReadOnlyCollection<long> rideIds,
        IReadOnlyCollection<long> stopIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);
}

public sealed class StrideRideScheduleSource(IHttpClientFactory httpClientFactory, ILogger<StrideRideScheduleSource> logger)
    : IRideScheduleSource
{
    private const string BaseAddress = "https://open-bus-stride-api.hasadna.org.il/";
    private const int MaxResponseBytes = 1_048_576;
    private const int MaxRows = 200;
    private const int MaxIds = 80;
    private const int MaxStringLength = 160;

    public async Task<IReadOnlyList<RideStop>> GetStopDeparturesAsync(
        long stopId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var uri = $"{BaseAddress}gtfs_ride_stops/list?gtfs_stop_ids={stopId}&{Window(from, to)}&order_by=arrival_time%20asc&limit=100";
        return Parse(await GetBytesAsync(uri, cancellationToken));
    }

    public async Task<IReadOnlyList<RideStop>> GetRideStopsAsync(
        long rideId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var uri = $"{BaseAddress}gtfs_ride_stops/list?gtfs_ride_ids={rideId}&{Window(from, to)}&order_by=stop_sequence%20asc&limit={MaxRows}";
        return Parse(await GetBytesAsync(uri, cancellationToken));
    }

    public async Task<IReadOnlyList<RideStop>> GetRideStopsAtAsync(
        IReadOnlyCollection<long> rideIds,
        IReadOnlyCollection<long> stopIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        if (rideIds.Count == 0 || stopIds.Count == 0)
        {
            return [];
        }

        var rides = string.Join(',', rideIds.Take(MaxIds));
        var stops = string.Join(',', stopIds.Take(MaxIds));
        var uri = $"{BaseAddress}gtfs_ride_stops/list?gtfs_ride_ids={rides}&gtfs_stop_ids={stops}&{Window(from, to)}&limit={MaxRows}";
        return Parse(await GetBytesAsync(uri, cancellationToken));
    }

    public static IReadOnlyList<RideStop> Parse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            MaxDepth = 16,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Stride ride-stops response must be an array.");
        }

        var rows = new List<RideStop>();
        foreach (var row in document.RootElement.EnumerateArray().Take(MaxRows))
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !TryGetLong(row, "gtfs_ride_id", out var rideId) ||
                !TryGetLong(row, "gtfs_stop_id", out var stopId) ||
                !TryGetLong(row, "stop_sequence", out var sequence) ||
                !row.TryGetProperty("arrival_time", out var timeProperty) || timeProperty.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(timeProperty.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                continue;
            }

            // Direct-trip matching needs only ride, stop, order and time; the line is absent on destination-side queries.
            var line = GetString(row, "gtfs_route__route_short_name") ?? string.Empty;
            rows.Add(new RideStop(
                rideId,
                stopId,
                (int)Math.Clamp(sequence, 0, 10_000),
                time,
                line,
                GetString(row, "gtfs_route__agency_name"),
                GetString(row, "gtfs_route__route_long_name"),
                GetString(row, "gtfs_stop__name"),
                GetNumberText(row, "gtfs_stop__code")));
        }

        return rows;
    }

    private static string Window(DateTimeOffset from, DateTimeOffset to) =>
        $"arrival_time_from={Utc(from)}&arrival_time_to={Utc(to)}";

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "%2B00:00";

    private async Task<byte[]> GetBytesAsync(string uri, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient("TransitApi");
            using var response = await client.GetAsync(new Uri(uri, UriKind.Absolute), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                throw new JsonException("Stride response exceeds the size limit.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length > MaxResponseBytes ? throw new JsonException("Stride response exceeds the size limit.") : bytes;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            logger.LogWarning(exception, "Stride ride schedule request failed.");
            throw;
        }
    }

    private static string? GetNumberText(JsonElement row, string name) =>
        row.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var value)
            ? value.ToString(CultureInfo.InvariantCulture)
            : null;

    private static bool TryGetLong(JsonElement row, string name, out long value)
    {
        value = 0;
        return row.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out value);
    }

    private static string? GetString(JsonElement row, string name)
    {
        if (!row.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Length <= MaxStringLength ? text : text[..MaxStringLength];
    }
}
