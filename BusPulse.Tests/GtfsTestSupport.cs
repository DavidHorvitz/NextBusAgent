using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using BusPulse.Services.Trips.Gtfs;
using Microsoft.Extensions.Logging.Abstractions;

namespace BusPulse.Tests;

/// <summary>Shared helpers for the GTFS feed tests: a scratch cache directory, in-memory zips and a stub HTTP client.</summary>
internal sealed class GtfsScratch : IDisposable
{
    public GtfsScratch()
    {
        Directory = Path.Combine(Path.GetTempPath(), "buspulse-gtfs-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    /// <summary>2026-11-07 12:00 Israel time, the day the fixture was cut.</summary>
    public static DateTimeOffset Noon { get; } = new(2026, 11, 7, 12, 0, 0, TimeSpan.FromHours(2));

    public static string FixturePath { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "gtfs-mini.zip");

    public static byte[] FixtureBytes() => File.ReadAllBytes(FixturePath);

    /// <summary>Puts a feed in the cache as gtfs-2026-11-07.zip, written <paramref name="age"/> before <paramref name="now"/>.</summary>
    public string PlaceCached(byte[] zip, DateTimeOffset now, TimeSpan age, string name = "gtfs-2026-11-07.zip")
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllBytes(path, zip);
        File.SetLastWriteTimeUtc(path, (now - age).UtcDateTime);
        return path;
    }

    public GtfsFeedScheduleSource Source(StubFactory factory, FixedClock clock) =>
        new(factory, clock, NullLogger<GtfsFeedScheduleSource>.Instance, Directory);

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Builds a zip from file name to text content.</summary>
    public static byte[] Zip(IReadOnlyDictionary<string, string> files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in files)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
        }

        return stream.ToArray();
    }

    /// <summary>A minimal valid feed with the given trips; stop_times lines are "trip,arrival,departure,stop_id,sequence".</summary>
    public static byte[] MiniFeed(string calendar, string trips, string stopTimes, string? calendarDates = null, string? stops = null, string? routes = null)
    {
        var files = new Dictionary<string, string>
        {
            ["agency.txt"] = "agency_id,agency_name\r\n1,Test Agency\r\n",
            ["routes.txt"] = routes ?? "route_id,agency_id,route_short_name,route_long_name\r\n10,1,7,Long Name\r\n",
            ["stops.txt"] = stops ?? "stop_id,stop_code,stop_name\r\n100,1001,First\r\n101,1002,Second\r\n",
            ["trips.txt"] = trips,
            ["stop_times.txt"] = stopTimes,
            ["calendar.txt"] = calendar,
        };
        if (calendarDates is not null)
        {
            files["calendar_dates.txt"] = calendarDates;
        }

        return Zip(files);
    }
}

/// <summary>Counts requests and answers each with <paramref name="respond"/>.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public Uri? LastUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        LastUri = request.RequestUri;
        return await respond(request);
    }

    public static StubHandler Ok(byte[] body, TimeSpan? delay = null) => new(async _ =>
    {
        if (delay is { } wait)
        {
            await Task.Delay(wait);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    });

    public static StubHandler Status(HttpStatusCode status) => new(_ => Task.FromResult(new HttpResponseMessage(status)));
}

internal sealed class StubFactory(StubHandler handler) : IHttpClientFactory
{
    public StubHandler Handler { get; } = handler;

    public string? RequestedClientName { get; private set; }

    public HttpClient CreateClient(string name)
    {
        RequestedClientName = name;
        return new HttpClient(Handler, disposeHandler: false);
    }

    /// <summary>A factory that fails the test when any request is made (the feed must come from the cache).</summary>
    public static StubFactory NoNetwork() => new(new StubHandler(_ => throw new InvalidOperationException("no network expected")));
}

/// <summary>HTTP content that claims a length without carrying the bytes (for the size cap).</summary>
internal sealed class ClaimedLengthContent(long length) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;

    protected override bool TryComputeLength(out long result)
    {
        result = length;
        return true;
    }
}
