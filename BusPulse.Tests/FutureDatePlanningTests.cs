using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using BusPulse.Services.Trips.Gtfs;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using BusPulse.Services.LiveData;

namespace BusPulse.Tests;

/// <summary>Stride holds no timetable after today, so a later date is planned from the official feed (stop ids are stop codes).</summary>
public sealed class FutureDatePlanningTests
{
    // Tuesday 2026-11-03 12:00 in Israel; the target is the next Tuesday at 08:00 local time.
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T10:00:00+00:00");
    private static readonly DateTimeOffset NextWeek = DateTimeOffset.Parse("2026-11-10T06:00:00+00:00");

    // The Stride ids (1, 2) must NOT be used for the feed: it knows stops by code (100, 200).
    private static readonly Stop StopA = new("100", "Origin stop", "Bnei Brak", 32.1000, 34.8000, Id: 1);
    private static readonly Stop StopB = new("200", "Destination stop", "Tel Aviv", 32.1100, 34.8000, Id: 2);

    [Fact]
    public async Task FutureDate_WithAReadyFeed_IsPlannedFromTheFeed_ByStopCode_AndNeverLive()
    {
        var f = new Fixture(FeedState.Ready);
        f.Feed.Inner.Departures[100] = [new RideStop(77, 100, 2, NextWeek.AddMinutes(30), "4", "Dan", "A<->B-City-1")];
        f.Feed.Inner.DestinationRows = [new RideStop(77, 200, 5, NextWeek.AddMinutes(50), string.Empty, null, null)];

        var plan = await f.Planner.PlanAsync(Request(TripTimeMode.DepartAt, NextWeek));

        var option = Assert.Single(plan.Options);
        Assert.Equal(TripPlanStatus.Ok, plan.Status);
        Assert.Equal(77, option.RideId);
        Assert.False(option.IsLive);
        Assert.Equal(ArrivalSource.Schedule, option.Source);
        Assert.Equal([100L], f.Feed.Inner.DepartureStopIds);
        Assert.Equal([new DateOnly(2026, 11, 10)], f.Feed.PreparedDates);
        Assert.Empty(f.Stride.DepartureStopIds);
        Assert.Equal(0, f.Aggregator.Calls);
    }

    [Theory]
    [InlineData(FeedState.DateOutsideFeed)]
    [InlineData(FeedState.NoServiceOnDate)]
    public async Task FutureDate_BeyondTheFeed_SaysThereIsNoTimetable(FeedState state)
    {
        var f = new Fixture(state);

        var plan = await f.Planner.PlanAsync(Request(TripTimeMode.DepartAt, NextWeek));

        Assert.Equal(TripPlanStatus.NoScheduleForDate, plan.Status);
        Assert.Empty(f.Feed.Inner.DepartureStopIds);
    }

    [Theory]
    [InlineData(FeedState.Failed)]
    [InlineData(FeedState.Downloading)]
    public async Task FutureDate_WhenTheFeedCannotBePrepared_SaysSoInsteadOfNoTrips(FeedState state)
    {
        var f = new Fixture(state);

        var plan = await f.Planner.PlanAsync(Request(TripTimeMode.DepartAt, NextWeek));

        Assert.Equal(TripPlanStatus.ScheduleUnavailable, plan.Status);
        Assert.Empty(plan.Options);
    }

    [Fact]
    public async Task FutureDate_ReadyFeedWithNoDeparturesNearby_IsJustNoOptions()
    {
        var f = new Fixture(FeedState.Ready);

        var plan = await f.Planner.PlanAsync(Request(TripTimeMode.DepartAt, NextWeek));

        Assert.Equal(TripPlanStatus.NoOptions, plan.Status);
    }

    [Fact]
    public async Task Today_NeverTouchesTheFeed_AndKeepsUsingStride()
    {
        var f = new Fixture(FeedState.Ready);
        f.Stride.Departures[1] = [new RideStop(10, 1, 2, Now.AddMinutes(10), "4", "Dan", "A<->B-City-1")];
        f.Stride.DestinationRows = [new RideStop(10, 2, 5, Now.AddMinutes(25), string.Empty, null, null)];

        var plan = await f.Planner.PlanAsync(Request(TripTimeMode.Now, null));

        Assert.Equal(10, Assert.Single(plan.Options).RideId);
        Assert.Empty(f.Feed.PreparedDates);
        Assert.Empty(f.Feed.Inner.DepartureStopIds);
        Assert.Equal([1L], f.Stride.DepartureStopIds);
    }

    [Fact]
    public async Task ArriveBy_OnAFutureDate_UsesTheFeedWithItsLatestDeparturesFirst()
    {
        var f = new Fixture(FeedState.Ready);
        f.Feed.Inner.Departures[100] = Enumerable.Range(1, 150).Select(i => new RideStop(i, 100, 2, NextWeek.AddMinutes(-170 + i), "4", "Dan", "A<->B-City-1")).ToArray();
        f.Feed.Inner.DestinationRows = [new RideStop(145, 200, 5, NextWeek.AddMinutes(-10), string.Empty, null, null)];

        var plan = await f.Planner.PlanAsync(Request(TripTimeMode.ArriveBy, NextWeek));

        // Only the 100 latest of the 150 departures are fetched, and ride 145 is among them.
        Assert.Equal(145, Assert.Single(plan.Options).RideId);
    }

    private static TripRequest Request(TripTimeMode mode, DateTimeOffset? target) =>
        new(32.1000, 34.8000, 32.1100, 34.8000, mode, target);

    private sealed class Fixture
    {
        public Fixture(FeedState state)
        {
            Stride = new TripPlannerServiceTests.FakeRideSchedule();
            Feed = new FakeFeed(state);
            Aggregator = new TripPlannerServiceTests.FakeAggregator();
            Planner = new TripPlannerService(
                new FakeCatalog(StopA, StopB), Stride, Aggregator, new FixedClock(Now), NullLogger<TripPlannerService>.Instance, Feed);
        }

        public TripPlannerServiceTests.FakeRideSchedule Stride { get; }
        public FakeFeed Feed { get; }
        public TripPlannerServiceTests.FakeAggregator Aggregator { get; }
        public TripPlannerService Planner { get; }
    }

    /// <summary>A feed that answers with a fixed state and serves rows from an in-memory schedule.</summary>
    internal sealed class FakeFeed(FeedState state) : IFutureScheduleSource
    {
        public TripPlannerServiceTests.FakeRideSchedule Inner { get; } = new();
        public List<DateOnly> PreparedDates { get; } = [];
        public bool IsDownloading => state == FeedState.Downloading;

        public Task<FeedAvailability> PrepareAsync(DateOnly date, IProgress<string>? progress, CancellationToken cancellationToken)
        {
            PreparedDates.Add(date);
            return Task.FromResult(new FeedAvailability(state, new DateOnly(2026, 11, 7), new DateOnly(2026, 12, 7), state == FeedState.Failed ? "offline" : null));
        }

        public Task<IReadOnlyList<RideStop>> GetStopDeparturesAsync(long stopId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default, bool latestFirst = false) =>
            Inner.GetStopDeparturesAsync(stopId, from, to, cancellationToken, latestFirst);

        public Task<IReadOnlyList<RideStop>> GetRideStopsAsync(long rideId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Inner.GetRideStopsAsync(rideId, from, to, cancellationToken);

        public Task<IReadOnlyList<RideStop>> GetRideStopsAtAsync(IReadOnlyCollection<long> rideIds, IReadOnlyCollection<long> stopIds, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Inner.GetRideStopsAtAsync(rideIds, stopIds, from, to, cancellationToken);
    }
}
