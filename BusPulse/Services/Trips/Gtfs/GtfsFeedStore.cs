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
/// file and moved into place only after the zip passes validation. A copy younger than 24 hours is reused; when a
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

    /// <summary>Path of a fresh, validated feed zip, downloading it first when needed.</summary>
    public async Task<string> EnsureFreshAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        Task<string> task;
        lock (_gate)
        {
            _inflight = task = _inflight is { IsCompleted: false } running ? running : Task.Run(() => RefreshAsync(progress), CancellationToken.None);
        }

        return await task.WaitAsync(cancellationToken);
    }

    private async Task<string> RefreshAsync(IProgress<string>? progress)
    {
        Directory.CreateDirectory(cacheDirectory);
        var existing = Cached().Where(IsValidZip).ToList();
        var now = clock.UtcNow;
        if (existing.Count > 0 && now - File.GetLastWriteTimeUtc(existing[0]) < FreshFor)
        {
            Cleanup(existing[0]);
            return existing[0];
        }

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
            Cleanup(target);
            return target;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or GtfsFeedException or InvalidDataException)
        {
            TryDelete(temp);
            if (existing.Count > 0)
            {
                logger.LogWarning(exception, "GTFS feed refresh failed; keeping {Path}.", existing[0]);
                return existing[0];
            }

            throw exception as GtfsFeedException ?? new GtfsFeedException($"The timetable could not be downloaded: {exception.Message}", exception);
        }
    }

    private async Task DownloadAsync(string temp, IProgress<string>? progress)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        using var response = await client.GetAsync(FeedUri, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new GtfsFeedException($"The timetable server answered {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
        {
            throw new GtfsFeedException("The timetable file is larger than the allowed size.");
        }

        await using var source = await response.Content.ReadAsStreamAsync();
        await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        long reportedAt = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
            {
                throw new GtfsFeedException("The timetable file is larger than the allowed size.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read));
            if (total - reportedAt >= 10 * 1024 * 1024)
            {
                reportedAt = total;
                progress?.Report($"Downloading the timetable: {total / (1024 * 1024)} MB");
            }
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
