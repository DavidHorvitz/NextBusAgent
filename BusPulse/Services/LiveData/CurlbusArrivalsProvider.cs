using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.LiveData;

/// <summary>
/// Unofficial community service (curlbus.app) with no SLA: responses are cached per stop, requests are
/// rate-limited, and repeated failures open a circuit breaker.
/// </summary>
public sealed class CurlbusArrivalsProvider(
    IHttpClientFactory httpClientFactory,
    IClock clock,
    ILogger<CurlbusArrivalsProvider> logger) : ILiveArrivalsProvider
{
    public const string ClientName = "Curlbus";

    private const string BaseAddress = "https://curlbus.app/";
    private const int MaxResponseBytes = 512 * 1024;
    private const int MaxStringLength = 128;
    private const int MaxArrivalsPerStop = 50;
    private const int FailuresBeforeCircuitOpens = 3;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MinRequestSpacing = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MaxFutureEta = TimeSpan.FromHours(6);
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);
    private static readonly string[] TimestampFormats = ["yyyy-MM-dd HH:mm:sszzz", "yyyy-MM-ddTHH:mm:sszzz"];

    private readonly ConcurrentDictionary<string, (DateTimeOffset FetchedAt, ProviderResult Result)> _cache = new();
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private DateTimeOffset _circuitOpenUntil = DateTimeOffset.MinValue;

    public ArrivalSource Source => ArrivalSource.Curlbus;

    public async Task<ProviderResult> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default)
    {
        if (!IsValidStopCode(stopCode))
        {
            return ProviderResult.Failed(ProviderStatus.Unavailable, "Invalid stop code.");
        }

        var now = clock.UtcNow;
        if (_cache.TryGetValue(stopCode, out var cached) && now - cached.FetchedAt < CacheDuration)
        {
            return cached.Result;
        }

        if (now < _circuitOpenUntil)
        {
            return ProviderResult.Failed(ProviderStatus.CircuitOpen, "Curlbus is paused after repeated failures.");
        }

        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed this stop, or opened the circuit, while this one waited.
            var afterWait = clock.UtcNow;
            if (_cache.TryGetValue(stopCode, out var fresh) && afterWait - fresh.FetchedAt < CacheDuration)
            {
                return fresh.Result;
            }

            if (afterWait < _circuitOpenUntil)
            {
                return ProviderResult.Failed(ProviderStatus.CircuitOpen, "Curlbus is paused after repeated failures.");
            }

            var wait = _lastRequestAt + MinRequestSpacing - clock.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            _lastRequestAt = clock.UtcNow;
            var result = await FetchAsync(stopCode, cancellationToken);
            if (result.Status is ProviderStatus.Ok or ProviderStatus.NoData)
            {
                _consecutiveFailures = 0;
                _cache[stopCode] = (clock.UtcNow, result);
            }
            else
            {
                RegisterFailure();
            }

            return result;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public IReadOnlyList<Arrival> Parse(string stopCode, ReadOnlySpan<byte> json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            MaxDepth = 32,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Curlbus response must be an object.");
        }

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            throw new JsonException("Curlbus reported an error for this stop.");
        }

        if (!root.TryGetProperty("visits", out var visits) ||
            visits.ValueKind != JsonValueKind.Object ||
            !visits.TryGetProperty(stopCode, out var stopVisits) ||
            stopVisits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var arrivals = new List<Arrival>();
        foreach (var visit in stopVisits.EnumerateArray().Take(MaxArrivalsPerStop))
        {
            if (TryParseVisit(stopCode, visit, now, out var arrival))
            {
                arrivals.Add(arrival);
            }
        }

        return arrivals.OrderBy(arrival => arrival.Eta).ToArray();
    }

    private async Task<ProviderResult> FetchAsync(string stopCode, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseAddress + stopCode, UriKind.Absolute));
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                throw new JsonException("Curlbus response exceeds the size limit.");
            }

            var bytes = await ReadCappedAsync(response, cancellationToken);
            var arrivals = Parse(stopCode, bytes, clock.UtcNow);
            if (arrivals.Count == 0)
            {
                logger.LogInformation("Curlbus returned no valid arrivals for stop {StopCode}.", stopCode);
                return new ProviderResult(ProviderStatus.NoData, []);
            }

            logger.LogDebug("Curlbus returned {Count} arrivals for stop {StopCode}.", arrivals.Count, stopCode);
            return new ProviderResult(ProviderStatus.Ok, arrivals);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            logger.LogWarning(exception, "Curlbus request failed for stop {StopCode}.", stopCode);
            return ProviderResult.Failed(ProviderStatus.Unavailable, exception.GetType().Name);
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
                throw new JsonException("Curlbus response exceeds the size limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private void RegisterFailure()
    {
        if (++_consecutiveFailures >= FailuresBeforeCircuitOpens)
        {
            _circuitOpenUntil = clock.UtcNow + CircuitOpenDuration;
            _consecutiveFailures = 0;
            logger.LogWarning("Curlbus circuit opened for {Minutes} minutes.", CircuitOpenDuration.TotalMinutes);
        }
    }

    private bool TryParseVisit(string stopCode, JsonElement visit, DateTimeOffset now, out Arrival arrival)
    {
        arrival = null!;
        if (visit.ValueKind != JsonValueKind.Object ||
            !TryGetString(visit, "line_name", out var lineName) ||
            !TryGetTimestamp(visit, "eta", out var eta))
        {
            logger.LogDebug("Curlbus visit skipped: missing line or ETA.");
            return false;
        }

        if (eta < now - ClockSkewTolerance || eta > now + MaxFutureEta)
        {
            logger.LogDebug("Curlbus visit skipped: ETA {Eta:o} outside the valid window.", eta);
            return false;
        }

        var observedAt = TryGetTimestamp(visit, "timestamp", out var observed) && observed <= now + ClockSkewTolerance
            ? observed
            : now;

        arrival = new Arrival(
            stopCode,
            lineName,
            ReadOperator(visit),
            ReadDestination(visit),
            eta,
            IsLive: true,
            ArrivalSource.Curlbus,
            observedAt);
        return true;
    }

    private static string? ReadOperator(JsonElement visit) =>
        Navigate(visit, "static_info", "agency", "name", "HE") ?? Navigate(visit, "static_info", "agency", "name", "EN");

    private static string? ReadDestination(JsonElement visit) =>
        Navigate(visit, "static_info", "route", "destination", "name", "HE") ??
        Navigate(visit, "static_info", "route", "destination", "name", "EN");

    private static string? Navigate(JsonElement element, params string[] path)
    {
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? Truncate(element.GetString()) : null;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = Truncate(property.GetString());
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryGetTimestamp(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParseExact(
                property.GetString(),
                TimestampFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out value);
    }

    private static string? Truncate(string? text) =>
        text is null ? null : text.Length <= MaxStringLength ? text : text[..MaxStringLength];

    private static bool IsValidStopCode(string stopCode) =>
        !string.IsNullOrWhiteSpace(stopCode) && stopCode.Length <= 12 && stopCode.All(char.IsAsciiDigit);
}
