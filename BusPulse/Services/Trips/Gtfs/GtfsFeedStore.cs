using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Trips.Gtfs;

/// <summary>The feed could not be fetched or is not a usable GTFS zip, and no earlier copy exists to fall back on.</summary>
public sealed class GtfsFeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Keeps one local copy of the Ministry of Transport GTFS zip under the cache directory as gtfs-yyyy-MM-dd.zip.
/// The download is HTTPS only from a fixed host (redirects are off in the named client), size-capped, written to a temp
/// file and moved into place only after the zip passes validation. A copy younger than 24 hours is reused, an older one is served while a background refresh runs; when a
/// refresh fails the previous copy is kept and used. Files older than 3 days are deleted. One download runs at a time
/// and concurrent callers share it.
/// </summary>
internal sealed class GtfsFeedStore(IHttpClientFactory httpClientFactory, IClock clock, ILogger logger, string cacheDirectory)
{
    public const string ClientName = "GtfsFeed";
    public static readonly Uri FeedUri = new("https://gtfs.mot.gov.il/gtfsfiles/israel-public-transportation.zip");

    public const long MaxDownloadBytes = 400L * 1024 * 1024;
    private static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(3);
    private static readonly string[] RequiredEntries = ["agency.txt", "routes.txt", "trips.txt", "stops.txt", "stop_times.txt"];

    private readonly object _gate = new();
    private Task<string>? _inflight;
    private DateTimeOffset? _failedAt;

    /// <summary>The download is abandoned when no bytes arrive for this long (a half-open connection never errors on its own).</summary>
    internal TimeSpan IdleReadTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Hard cap on one whole download.</summary>
    internal TimeSpan OverallTimeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>After a failed refresh no new download starts for this long while a usable copy exists.</summary>
    internal TimeSpan FailureBackoff { get; set; } = TimeSpan.FromHours(1);

    public bool IsDownloading
    {
        get
        {
            lock (_gate)
            {
                return _inflight is { IsCompleted: false };
            }
        }
    }

    /// <summary>
    /// Path of a usable, validated feed zip. A copy under 24 hours old is returned as is. An older copy still inside the
    /// 3-day retention is returned immediately while a refresh runs in the background (unless a refresh failed within the
    /// backoff window). Only when no usable copy exists does the caller wait for the download.
    /// </summary>
    public async Task<string> EnsureFreshAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var existing = await Task.Run(() =>
        {
            Directory.CreateDirectory(cacheDirectory);
            return Cached().FirstOrDefault(IsValidZip);
        }, CancellationToken.None).WaitAsync(cancellationToken);

        var now = clock.UtcNow;
        if (existing is not null)
        {
            var age = now - File.GetLastWriteTimeUtc(existing);
            if (age < FreshFor)
            {
                Cleanup(existing);
                return existing;
            }

            if (InBackoff(now))
            {
                return existing;
            }

            if (age < KeepFor)
            {
                var background = StartRefresh(null, existing);
                _ = background.ContinueWith(
                    finished => logger.LogDebug(finished.Exception, "GTFS background refresh ended unexpectedly."),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
                return existing;
            }
        }

        return await StartRefresh(progress, existing).WaitAsync(cancellationToken);
    }

    private bool InBackoff(DateTimeOffset now)
    {
        lock (_gate)
        {
            return _failedAt is { } failed && now - failed < FailureBackoff;
        }
    }

    private Task<string> StartRefresh(IProgress<string>? progress, string? existing)
    {
        lock (_gate)
        {
            return _inflight = _inflight is { IsCompleted: false } running ? running : Task.Run(() => RefreshAsync(progress, existing), CancellationToken.None);
        }
    }

    private async Task<string> RefreshAsync(IProgress<string>? progress, string? existing)
    {
        var now = clock.UtcNow;
        var israelToday = GtfsTime.LocalDate(now);
        var target = Path.Combine(cacheDirectory, $"gtfs-{israelToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.zip");
        var temp = Path.Combine(cacheDirectory, $"gtfs-download-{Guid.NewGuid():N}.tmp");
        try
        {
            await DownloadAsync(temp, progress);
            if (!IsValidZip(temp))
            {
                throw new GtfsFeedException("The downloaded file is not a valid GTFS zip.");
            }

            File.Move(temp, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, now.UtcDateTime);
            lock (_gate)
            {
                _failedAt = null;
            }

            Cleanup(target);
            return target;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException or GtfsFeedException or InvalidDataException)
        {
            TryDelete(temp);
            lock (_gate)
            {
                _failedAt = clock.UtcNow;
            }

            if (existing is not null)
            {
                logger.LogWarning(exception, "GTFS feed refresh failed; keeping {Path}.", existing);
                return existing;
            }

            throw exception as GtfsFeedException ?? new GtfsFeedException($"The timetable could not be downloaded: {exception.Message}", exception);
        }
    }

    private async Task DownloadAsync(string temp, IProgress<string>? progress)
    {
        using var overall = new CancellationTokenSource(OverallTimeout);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        var token = idle.Token;
        idle.CancelAfter(IdleReadTimeout);
        var client = httpClientFactory.CreateClient(ClientName);
        try
        {
            using var response = await client.GetAsync(FeedUri, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode)
            {
                throw new GtfsFeedException($"The timetable server answered {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            {
                throw new GtfsFeedException("The timetable file is larger than the allowed size.");
            }

            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long total = 0;
            long reportedAt = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                idle.CancelAfter(IdleReadTimeout);
                total += read;
                if (total > MaxDownloadBytes)
                {
                    throw new GtfsFeedException("The timetable file is larger than the allowed size.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), token);
                if (total - reportedAt >= 10 * 1024 * 1024)
                {
                    reportedAt = total;
                    progress?.Report($"Downloading the timetable: {total / (1024 * 1024)} MB");
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new GtfsFeedException("The timetable download stalled or took too long.");
        }
    }

    /// <summary>True when the zip opens and holds every file the index needs (calendar.txt or calendar_dates.txt too).</summary>
    public static bool IsValidZip(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var names = zip.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
            return RequiredEntries.All(names.Contains) && (names.Contains("calendar.txt") || names.Contains("calendar_dates.txt"));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Cached feed files, newest first.</summary>
    private List<string> Cached() =>
        Directory.EnumerateFiles(cacheDirectory, "gtfs-????-??-??.zip")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToList();

    private void Cleanup(string keep)
    {
        try
        {
            var limit = clock.UtcNow - KeepFor;
            foreach (var path in Directory.EnumerateFiles(cacheDirectory, "gtfs-*"))
            {
                if (!string.Equals(path, keep, StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(path) < limit.UtcDateTime)
                {
                    TryDelete(path);
                }
            }
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "GTFS cache cleanup failed.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort only.
        }
    }
}
