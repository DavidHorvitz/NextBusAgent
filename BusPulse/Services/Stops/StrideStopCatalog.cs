using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Stops;

public interface IStopCatalog
{
    Task<IReadOnlyList<Stop>> GetStopsAsync(CancellationToken cancellationToken = default);

    /// <summary>The list already in memory, without waiting for a download; null before the first load.</summary>
    IReadOnlyList<Stop>? Loaded { get; }
}

/// <summary>
/// All stops valid today, from the Stride GTFS static tables. The API has no geographic filter, so the full list
/// (about 30k stops) is downloaded once per day and cached under %LOCALAPPDATA%\BusPulse\cache.
/// </summary>
public sealed class StrideStopCatalog(
    IHttpClientFactory httpClientFactory,
    IClock clock,
    ILogger<StrideStopCatalog> logger,
    string? cacheDirectory = null) : IStopCatalog
{
    private const string BaseAddress = "https://open-bus-stride-api.hasadna.org.il/";
    /// <summary>A long-timeout client: a single 5,000-stop page can take close to a minute when Stride is slow.</summary>
    public const string BulkClientName = "StrideBulk";

    private const int PageSize = 5000;
    private const int MaxPages = 12;
    private const int MaxPageBytes = 1_048_576;
    private const int MaxNameLength = 128;

    private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusPulse", "cache");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<Stop>? _loaded;
    private Task? _refresh;
    private string? _loadedDate;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(10);

    public IReadOnlyList<Stop>? Loaded => _loaded;

    /// <summary>
    /// Today's list when it is cached. Otherwise the most recent earlier cache (stops rarely move) is returned at once and
    /// today's list downloads in the background, because Stride can take minutes to serve all pages. Only the very first run,
    /// with no cache at all, waits for the download.
    /// </summary>
    public async Task<IReadOnlyList<Stop>> GetStopsAsync(CancellationToken cancellationToken = default)
    {
        var date = IsraelDate(clock.UtcNow);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded is not null && _loadedDate == date)
            {
                return _loaded;
            }

            var cachePath = Path.Combine(_cacheDirectory, $"stops-{date}.json");
            if (TryReadCache(cachePath) is { } todays)
            {
                _loaded = todays;
                _loadedDate = date;
                return todays;
            }

            var fallback = _loaded ?? TryReadLatestCache(date);
            if (fallback is not null)
            {
                _loaded = fallback;
                StartBackgroundRefresh(date, cachePath);
                return fallback;
            }

            var stops = await DownloadAsync(date, cachePath, cancellationToken);
            if (stops.Count == 0)
            {
                throw new InvalidDataException("Stride returned no stops for today.");
            }

            _loaded = stops;
            _loadedDate = date;
            return stops;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Completes when the current background refresh (if any) has finished; used by tests and diagnostics.</summary>
    public Task WhenRefreshedAsync() => _refresh ?? Task.CompletedTask;

    private void StartBackgroundRefresh(string date, string cachePath)
    {
        if (_refresh is { IsCompleted: false } || clock.UtcNow < _retryAfter)
        {
            return;
        }

        _refresh = Task.Run(async () =>
        {
            try
            {
                var stops = await DownloadAsync(date, cachePath, CancellationToken.None);
                if (stops.Count == 0)
                {
                    throw new InvalidDataException("Stride returned no stops for today.");
                }

                await _gate.WaitAsync();
                try
                {
                    _loaded = stops;
                    _loadedDate = date;
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
            {
                logger.LogWarning(exception, "Background refresh of the stop list for {Date} failed; keeping the previous list.", date);
                _retryAfter = clock.UtcNow + RetryInterval;
            }
        });
    }

    /// <summary>The newest cache file from the last 30 days, if any.</summary>
    private IReadOnlyList<Stop>? TryReadLatestCache(string today)
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory) ||
                !DateTime.TryParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var todayDate))
            {
                return null;
            }

            var newest = Directory.EnumerateFiles(_cacheDirectory, "stops-*.json")
                .Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path)["stops-".Length..]))
                .Select(file => (file.Path, Ok: DateTime.TryParseExact(file.Name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day), Day: day))
                .Where(file => file.Ok && file.Day <= todayDate && file.Day >= todayDate.AddDays(-30))
                .OrderByDescending(file => file.Day)
                .FirstOrDefault();
            return newest.Path is null ? null : TryReadCache(newest.Path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not look for an earlier stop cache.");
            return null;
        }
    }

    public static IReadOnlyList<Stop> ParseStops(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            MaxDepth = 16,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Stride stops response must be an array.");
        }

        var stops = new List<Stop>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt64(out var codeValue) || codeValue <= 0 ||
                !row.TryGetProperty("lat", out var lat) || lat.ValueKind != JsonValueKind.Number ||
                !row.TryGetProperty("lon", out var lon) || lon.ValueKind != JsonValueKind.Number ||
                !row.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var latitude = lat.GetDouble();
            var longitude = lon.GetDouble();
            if (!Geo.IsInIsrael(latitude, longitude) || string.IsNullOrWhiteSpace(name.GetString()))
            {
                continue;
            }

            var id = row.TryGetProperty("id", out var idProperty) && idProperty.ValueKind == JsonValueKind.Number && idProperty.TryGetInt64(out var idValue)
                ? idValue
                : 0;
            var city = row.TryGetProperty("city", out var cityProperty) && cityProperty.ValueKind == JsonValueKind.String
                ? Truncate(cityProperty.GetString())
                : null;
            stops.Add(new Stop(codeValue.ToString(CultureInfo.InvariantCulture), Truncate(name.GetString())!, city, latitude, longitude, id));
        }

        return stops;
    }

    private IReadOnlyList<Stop>? TryReadCache(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var stops = ParseStops(File.ReadAllBytes(path));
            // Caches written before stop ids were stored are discarded and re-downloaded.
            return stops.Count > 0 && stops.All(stop => stop.Id > 0) ? stops : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Ignoring unreadable stop cache.");
            return null;
        }
    }

    private async Task<IReadOnlyList<Stop>> DownloadAsync(string date, string cachePath, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(BulkClientName);
        var byCode = new Dictionary<string, Stop>();
        for (var page = 0; page < MaxPages; page++)
        {
            var uri = new Uri(
                $"{BaseAddress}gtfs_stops/list?date_from={date}&date_to={date}&limit={PageSize}&offset={page * PageSize}",
                UriKind.Absolute);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxPageBytes)
            {
                throw new JsonException("Stride stops page exceeds the size limit.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length > MaxPageBytes)
            {
                throw new JsonException("Stride stops page exceeds the size limit.");
            }

            var stops = ParseStops(bytes);
            foreach (var stop in stops)
            {
                byCode[stop.Code] = stop;
            }

            if (JsonRowCount(bytes) < PageSize)
            {
                break;
            }
        }

        var all = byCode.Values.ToArray();
        if (all.Length > 0)
        {
            TryWriteCache(cachePath, all);
        }
        logger.LogInformation("Downloaded {Count} stops for {Date}.", all.Length, date);
        return all;
    }

    private void TryWriteCache(string path, IReadOnlyList<Stop> stops)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var rows = stops.Select(stop => new
            {
                id = stop.Id,
                code = long.Parse(stop.Code, CultureInfo.InvariantCulture),
                name = stop.Name,
                city = stop.City,
                lat = stop.Latitude,
                lon = stop.Longitude
            });
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(rows));
            File.Move(temp, path, overwrite: true);
            foreach (var old in Directory.EnumerateFiles(_cacheDirectory, "stops-*.json").Where(file => file != path))
            {
                File.Delete(old);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not write the stop cache.");
        }
    }

    private static int JsonRowCount(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetArrayLength();
    }

    private static string? Truncate(string? text) =>
        text is null ? null : text.Length <= MaxNameLength ? text : text[..MaxNameLength];

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
}
