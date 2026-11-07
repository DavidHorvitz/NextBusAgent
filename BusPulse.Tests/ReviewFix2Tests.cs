using System.IO;
using System.Net;
using System.Net.Http;
using BusPulse.Services.Secrets;
using BusPulse.Services.Trips;
using BusPulse.Services.Trips.Gtfs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class GtfsFeedHardeningTests
{
    private static GtfsFeedStore Store(GtfsScratch scratch, StubFactory factory, FixedClock clock) =>
        new(factory, clock, NullLogger.Instance, scratch.Directory) { IdleReadTimeout = TimeSpan.FromMilliseconds(300) };

    private static async Task WaitUntilIdle(GtfsFeedStore store)
    {
        for (var i = 0; i < 200 && store.IsDownloading; i++)
        {
            await Task.Delay(25);
        }

        Assert.False(store.IsDownloading);
    }

    private static StubHandler Stalled(Func<int>? onCall = null) => new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new StalledStream()),
    }));

    [Fact]
    public async Task A_body_that_stops_arriving_fails_the_download_and_leaves_no_file()
    {
        using var scratch = new GtfsScratch();
        var store = Store(scratch, new StubFactory(Stalled()), new FixedClock(GtfsScratch.Noon));

        var task = store.EnsureFreshAsync(null, default);
        var exception = await Assert.ThrowsAsync<GtfsFeedException>(() => task.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("stalled", exception.Message);
        Assert.Empty(Directory.GetFiles(scratch.Directory));
        Assert.False(store.IsDownloading);
    }

    [Fact]
    public async Task A_stalled_download_surfaces_as_failed_and_a_later_call_starts_a_new_download()
    {
        using var scratch = new GtfsScratch();
        var factory = new StubFactory(Stalled());
        var source = scratch.Source(factory, new FixedClock(GtfsScratch.Noon));
        // Source builds its own store with the default 60 s limit, so drive the store directly for the second call.
        var store = Store(scratch, factory, new FixedClock(GtfsScratch.Noon));

        await Assert.ThrowsAsync<GtfsFeedException>(() => store.EnsureFreshAsync(null, default).WaitAsync(TimeSpan.FromSeconds(10)));
        await Assert.ThrowsAsync<GtfsFeedException>(() => store.EnsureFreshAsync(null, default).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(2, factory.Handler.Calls);
        source.Dispose();
    }

    [Fact]
    public async Task A_stale_copy_is_served_at_once_while_the_refresh_is_still_pending()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        var cached = scratch.PlaceCached(GtfsScratch.FixtureBytes(), clock.UtcNow, TimeSpan.FromHours(30));
        var gate = new TaskCompletionSource<HttpResponseMessage>();
        var factory = new StubFactory(new StubHandler(_ => gate.Task));
        var store = Store(scratch, factory, clock);
        store.IdleReadTimeout = TimeSpan.FromMinutes(5);

        var path = await store.EnsureFreshAsync(null, default).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(cached, path);
        Assert.True(store.IsDownloading);
        gate.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(GtfsScratch.FixtureBytes()) });
        await WaitUntilIdle(store);
        Assert.Equal(1, factory.Handler.Calls);
        Assert.True(File.Exists(Path.Combine(scratch.Directory, "gtfs-2026-11-07.zip")));
    }

    [Fact]
    public async Task A_copy_older_than_the_retention_still_waits_for_the_download()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        scratch.PlaceCached(GtfsScratch.FixtureBytes(), clock.UtcNow, TimeSpan.FromDays(4), "gtfs-2026-11-03.zip");
        var factory = new StubFactory(StubHandler.Ok(GtfsScratch.FixtureBytes(), TimeSpan.FromMilliseconds(200)));
        var store = Store(scratch, factory, clock);

        var path = await store.EnsureFreshAsync(null, default);

        Assert.EndsWith("gtfs-2026-11-07.zip", path);
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Fact]
    public async Task After_a_failed_refresh_no_new_download_starts_for_an_hour_and_the_old_copy_is_kept()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        var cached = scratch.PlaceCached(GtfsScratch.FixtureBytes(), clock.UtcNow, TimeSpan.FromHours(30));
        var factory = new StubFactory(StubHandler.Status(HttpStatusCode.InternalServerError));
        var store = Store(scratch, factory, clock);

        Assert.Equal(cached, await store.EnsureFreshAsync(null, default));
        await WaitUntilIdle(store);
        Assert.Equal(1, factory.Handler.Calls);

        clock.UtcNow += TimeSpan.FromMinutes(6);
        Assert.Equal(cached, await store.EnsureFreshAsync(null, default));
        clock.UtcNow += TimeSpan.FromMinutes(50);
        Assert.Equal(cached, await store.EnsureFreshAsync(null, default));
        await WaitUntilIdle(store);
        Assert.Equal(1, factory.Handler.Calls);

        clock.UtcNow += TimeSpan.FromMinutes(10);
        Assert.Equal(cached, await store.EnsureFreshAsync(null, default));
        await WaitUntilIdle(store);
        Assert.Equal(2, factory.Handler.Calls);
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}

public sealed class TripTimeFallBackTests
{
    private static readonly TimeZoneInfo Zone = TripTime.IsraelZone;

    [Fact]
    public void An_ambiguous_autumn_time_takes_the_first_occurrence_with_the_summer_offset()
    {
        var instant = TripTime.Resolve(new DateOnly(2026, 10, 25), new TimeSpan(1, 30, 0), Zone);

        Assert.Equal(DateTimeOffset.Parse("2026-10-24T22:30:00+00:00"), instant);
    }

    [Fact]
    public void A_plan_window_for_an_ambiguous_time_overlaps_the_gtfs_mapping_of_the_same_wall_time()
    {
        var date = new DateOnly(2026, 10, 25);
        var planned = TripTime.Resolve(date, new TimeSpan(1, 30, 0), Zone);
        var feedStart = GtfsTime.At(date, 1 * 3600);
        var feedEnd = GtfsTime.At(date, 2 * 3600 - 1);

        Assert.Equal(feedStart.AddMinutes(30), planned);
        Assert.InRange(planned, feedStart, feedEnd);
    }
}

public sealed class InfisicalOptionsRedactionTests
{
    [Fact]
    public void ToString_never_contains_the_client_secret()
    {
        var options = InfisicalOptions.From(name => name switch
        {
            "CLIENT_ID" => "client-id-1",
            "CLIENT_SECRET" => "super-secret-value-123",
            "PROJECT_ID" => "proj1",
            _ => null,
        });

        var text = options.ToString();

        Assert.True(options.IsConfigured);
        Assert.DoesNotContain("super-secret-value-123", text);
        Assert.Contains("client-id-1", text);
    }
}