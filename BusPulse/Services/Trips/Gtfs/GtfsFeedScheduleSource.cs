using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Trips.Gtfs;

public enum FeedState
{
    /// <summary>The timetable for the date is loaded and can be queried.</summary>
    Ready,

    /// <summary>The date is before or after the days the feed covers (see FirstDate and LastDate).</summary>
    DateOutsideFeed,

    /// <summary>The date is inside the feed's range but the feed lists no trips for it.</summary>
    NoServiceOnDate,

    /// <summary>The feed file is being downloaded right now.</summary>
    Downloading,

    /// <summary>The feed could not be downloaded or read; Reason says why.</summary>
    Failed
}

/// <summary>Whether the official timetable can answer for a date. FirstDate and LastDate are the feed's covered range when known.</summary>
public sealed record FeedAvailability(FeedState State, DateOnly? FirstDate = null, DateOnly? LastDate = null, string? Reason = null);

/// <summary>A schedule source that can plan for dates after today, from the Ministry of Transport GTFS feed.</summary>
public interface IFutureScheduleSource : IRideScheduleSource
{
    /// <summary>
    /// Makes the timetable of <paramref name="date"/> (an Israeli calendar day) ready: downloads the feed if needed, then
    /// builds the in-memory index. Returns the state; queries for a date that is not Ready return no rows. Progress texts
    /// are short English status lines; the caller localizes its own wording from the returned state.
    /// </summary>
    Task<FeedAvailability> PrepareAsync(DateOnly date, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>True while the feed file is downloading.</summary>
    bool IsDownloading { get; }
}

/// <summary>
/// <see cref="IRideScheduleSource"/> over the official GTFS feed. Stop ids are stop codes (the numbers on the stop sign).
/// Ride ids are 64-bit hashes of trip_id plus service date (see <see cref="GtfsDayIndex"/>). At most two day indexes stay
/// in memory and an index is dropped after 15 idle minutes.
/// </summary>
public sealed class GtfsFeedScheduleSource : IFutureScheduleSource, IDisposable
{
    private const int MaxRows = 100;
    private const int MaxRideRows = 500;
    private const int MaxIndexes = 2;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(15);

    private readonly GtfsFeedStore _store;
    private readonly RouteColorCatalog? _routeColors;
    private string? _colorsFeed;
    private readonly IClock _clock;
    private readonly ILogger<GtfsFeedScheduleSource> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<DateOnly, CachedIndex> _indexes = [];
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly System.Threading.Timer _idleTimer;
    private (string Path, DateTime Stamp, GtfsServiceCalendar Calendar)? _calendar;
    private (string Path, DateTimeOffset CheckedAt)? _lastFeed;

    private sealed class CachedIndex(Task<GtfsDayIndex> task, string feedPath, DateTimeOffset usedAt)
    {
        public Task<GtfsDayIndex> Task { get; } = task;
        public string FeedPath { get; } = feedPath;
        public DateTimeOffset UsedAt { get; set; } = usedAt;
    }

    public GtfsFeedScheduleSource(
        IHttpClientFactory httpClientFactory,
        IClock clock,
        ILogger<GtfsFeedScheduleSource> logger,
        string? cacheDirectory = null,
        RouteColorCatalog? routeColors = null)
    {
        _routeColors = routeColors;
        _clock = clock;
        _logger = logger;
        _store = new GtfsFeedStore(httpClientFactory, clock, logger, cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusPulse", "cache"));
        _idleTimer = new System.Threading.Timer(_ => EvictIdle(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public bool IsDownloading => _store.IsDownloading;

    public async Task<FeedAvailability> PrepareAsync(DateOnly date, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var (availability, _) = await ResolveAsync(date, progress, cancellationToken);
        return availability;
    }

    public async Task<IReadOnlyList<RideStop>> GetStopDeparturesAsync(
        long stopId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default,
        bool latestFirst = false)
    {
        if (to < from || stopId is < 0 or > int.MaxValue)
        {
            return [];
        }

        var result = new List<RideStop>();
        foreach (var index in await IndexesForWindowAsync(from, to, cancellationToken))
        {
            var (low, high) = SecondsWindow(index.Date, from, to);
            result.AddRange(index.CallsAt((int)stopId, low, high).Where(row => row.Time >= from && row.Time <= to));
        }

        result.Sort(static (a, b) => a.Time.CompareTo(b.Time));
        return latestFirst ? result.AsEnumerable().Reverse().Take(MaxRows).ToList() : result.Take(MaxRows).ToList();
    }

    /// <summary>
    /// Always empty: the Lines tab asks for a ride by its Stride id and is today-only, and this source's ride ids exist only
    /// inside a day index for a future date. Direct-trip planning uses <see cref="GetRideStopsAtAsync"/> instead.
    /// </summary>
    public Task<IReadOnlyList<RideStop>> GetRideStopsAsync(
        long rideId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RideStop>>([]);

    public async Task<IReadOnlyList<RideStop>> GetRideStopsAtAsync(
        IReadOnlyCollection<long> rideIds,
        IReadOnlyCollection<long> stopIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        if (rideIds.Count == 0 || stopIds.Count == 0 || to < from)
        {
            return [];
        }

        var result = new List<RideStop>();
        foreach (var index in await IndexesForWindowAsync(from, to, cancellationToken))
        {
            var (low, high) = SecondsWindow(index.Date, from, to);
            result.AddRange(index.CallsOf(rideIds, stopIds, low, high).Where(row => row.Time >= from && row.Time <= to));
        }

        // Stable: ride order, then sequence, is kept for equal times.
        return result.Select((row, order) => (row, order))
            .OrderBy(item => item.row.Time)
            .ThenBy(item => item.order)
            .Select(item => item.row)
            .Take(MaxRideRows)
            .ToList();
    }

    /// <summary>Drops day indexes unused for 15 minutes (also called by a timer).</summary>
    internal void EvictIdle()
    {
        lock (_gate)
        {
            var limit = _clock.UtcNow - IdleLimit;
            foreach (var date in _indexes.Where(pair => pair.Value.UsedAt < limit).Select(pair => pair.Key).ToList())
            {
                _indexes.Remove(date);
            }
        }
    }

    internal int LoadedIndexCount
    {
        get
        {
            lock (_gate)
            {
                return _indexes.Count;
            }
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        _buildGate.Dispose();
    }

    private async Task<List<GtfsDayIndex>> IndexesForWindowAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var first = GtfsTime.LocalDate(from);
        var last = GtfsTime.LocalDate(to);
        var indexes = new List<GtfsDayIndex>();
        for (var date = first; date <= last && indexes.Count < 3; date = date.AddDays(1))
        {
            var (availability, index) = await ResolveAsync(date, null, cancellationToken);
            if (index is not null)
            {
                indexes.Add(index);
            }
            else
            {
                _logger.LogInformation("GTFS timetable not available for {Date}: {State} {Reason}", date, availability.State, availability.Reason);
            }
        }

        return indexes;
    }

    private async Task<(FeedAvailability, GtfsDayIndex?)> ResolveAsync(DateOnly date, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            if (_store.IsDownloading)
            {
                progress?.Report("Downloading the timetable");
            }

            path = await FeedPathAsync(progress, cancellationToken);
        }
        catch (GtfsFeedException exception)
        {
            return (new FeedAvailability(FeedState.Failed, Reason: exception.Message), null);
        }

        GtfsServiceCalendar calendar;
        try
        {
            calendar = LoadCalendar(path);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            _logger.LogWarning(exception, "GTFS calendar could not be read.");
            return (new FeedAvailability(FeedState.Failed, Reason: "The timetable file could not be read."), null);
        }

        if (calendar.Range is not { } range)
        {
            return (new FeedAvailability(FeedState.NoServiceOnDate, Reason: "The timetable lists no service days."), null);
        }

        if (date < range.First || date > range.Last)
        {
            return (new FeedAvailability(FeedState.DateOutsideFeed, range.First, range.Last), null);
        }

        progress?.Report($"Preparing the timetable for {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        try
        {
            var index = await GetIndexAsync(date, path, calendar, cancellationToken);
            return index.RideCount == 0
                ? (new FeedAvailability(FeedState.NoServiceOnDate, range.First, range.Last), null)
                : (new FeedAvailability(FeedState.Ready, range.First, range.Last), index);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            _logger.LogWarning(exception, "GTFS index build failed for {Date}.", date);
            return (new FeedAvailability(FeedState.Failed, range.First, range.Last, "The timetable file could not be read."), null);
        }
    }

    /// <summary>The feed path, re-checking the cache directory at most every 5 minutes (queries come in bursts).</summary>
    private async Task<string> FeedPathAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_lastFeed is { } last && _clock.UtcNow - last.CheckedAt < TimeSpan.FromMinutes(5) && File.Exists(last.Path))
            {
                return last.Path;
            }
        }

        var path = await _store.EnsureFreshAsync(progress, cancellationToken);
        if (_routeColors is not null && !string.Equals(_colorsFeed, path, StringComparison.OrdinalIgnoreCase))
        {
            // Small (routes and agency only); refreshed once per feed file.
            _colorsFeed = path;
            _ = Task.Run(() => _routeColors.RefreshFromZip(path), CancellationToken.None);
        }

        lock (_gate)
        {
            _lastFeed = (path, _clock.UtcNow);
        }

        return path;
    }

    private GtfsServiceCalendar LoadCalendar(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (_gate)
        {
            if (_calendar is { } known && known.Path == path && known.Stamp == stamp)
            {
                return known.Calendar;
            }
        }

        using var zip = ZipFile.OpenRead(path);
        using var calendarStream = Limited(zip, "calendar.txt", 50_000_000);
        using var datesStream = Limited(zip, "calendar_dates.txt", 100_000_000);
        var calendar = GtfsServiceCalendar.Load(calendarStream, datesStream);
        lock (_gate)
        {
            _calendar = (path, stamp, calendar);
        }

        return calendar;
    }

    private static Stream? Limited(ZipArchive zip, string name, long maxBytes) =>
        zip.GetEntry(name) is { } entry ? new GtfsLimitedStream(entry.Open(), maxBytes, name) : null;

    private async Task<GtfsDayIndex> GetIndexAsync(DateOnly date, string path, GtfsServiceCalendar calendar, CancellationToken cancellationToken)
    {
        Task<GtfsDayIndex> task;
        lock (_gate)
        {
            if (_indexes.TryGetValue(date, out var cached) && cached.FeedPath == path && !(cached.Task.IsFaulted || cached.Task.IsCanceled))
            {
                cached.UsedAt = _clock.UtcNow;
                task = cached.Task;
            }
            else
            {
                // Builds are serialized so two big builds never overlap in memory; callers share the same task.
                task = Task.Run(async () =>
                {
                    await _buildGate.WaitAsync();
                    try
                    {
                        return GtfsDayIndex.Build(path, date, calendar);
                    }
                    finally
                    {
                        // The build leaves ~150 MB of large temporary arrays; give them back before the next build starts.
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                        _buildGate.Release();
                    }
                }, CancellationToken.None);
                _indexes[date] = new CachedIndex(task, path, _clock.UtcNow);
                while (_indexes.Count > MaxIndexes)
                {
                    var oldest = _indexes.Where(pair => pair.Key != date).MinBy(pair => pair.Value.UsedAt).Key;
                    _indexes.Remove(oldest);
                }
            }
        }

        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            lock (_gate)
            {
                if (_indexes.TryGetValue(date, out var cached) && cached.Task == task)
                {
                    _indexes.Remove(date);
                }
            }

            throw;
        }
    }

    /// <summary>Second offsets inside the day for a window, widened by an hour for DST; callers filter exactly afterwards.</summary>
    private static (int Low, int High) SecondsWindow(DateOnly date, DateTimeOffset from, DateTimeOffset to)
    {
        var low = Math.Floor(GtfsTime.WallSeconds(date, from)) - 3600;
        var high = Math.Ceiling(GtfsTime.WallSeconds(date, to)) + 3600;
        return ((int)Math.Clamp(low, 0, GtfsTime.SecondsPerDay), (int)Math.Clamp(high, 0, GtfsTime.SecondsPerDay));
    }
}
