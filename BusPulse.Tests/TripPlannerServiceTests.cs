using System.IO;
using BusPulse.Services.LiveData;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class TripPlannerServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");

    // Origin and destination are exactly on their stops (zero walking) unless a test moves them.
    private static readonly Stop StopA = new("100", "Origin stop", "Bnei Brak", 32.1000, 34.8000, Id: 1);
    private static readonly Stop StopB = new("200", "Destination stop", "Tel Aviv", 32.1100, 34.8000, Id: 2);
    private static readonly Stop StopFar = new("300", "Elsewhere", "Haifa", 32.8000, 34.9900, Id: 3);

    [Fact]
    public async Task DirectLine_ProducesAnOptionWithLeaveByAndArrival()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = [Departure(ride: 10, minutes: 10, sequence: 2, line: "4")];
        f.Schedule.DestinationRows = [Row(ride: 10, stop: 2, minutes: 25, sequence: 5)];

        var plan = await f.Planner.PlanAsync(Request());

        var option = Assert.Single(plan.Options);
        Assert.Equal(TripPlanStatus.Ok, plan.Status);
        Assert.Equal("4", option.Line);
        Assert.Equal(Now.AddMinutes(10), option.BusDeparture);
        Assert.Equal(Now.AddMinutes(25), option.ArriveAt);
        // No walking, one minute buffer.
        Assert.Equal(Now.AddMinutes(9), option.LeaveBy);
        Assert.False(option.IsLive);
        Assert.Equal(ArrivalSource.Schedule, option.Source);
        Assert.Equal("Dan", option.Operator);
    }

    [Fact]
    public async Task WalkingTime_IsSubtractedFromLeaveBy_AndAddedToArrival()
    {
        var f = new Fixture(new Stop("100", "Origin stop", null, 32.1004, 34.8000, Id: 1), StopB);
        f.Schedule.Departures[1] = [Departure(ride: 10, minutes: 20, sequence: 2)];
        f.Schedule.DestinationRows = [Row(ride: 10, stop: 2, minutes: 40, sequence: 5)];

        var option = Assert.Single((await f.Planner.PlanAsync(Request())).Options);

        // About 44 m away: roughly 44 seconds of walking.
        Assert.InRange(option.WalkToStop.TotalSeconds, 40, 50);
        Assert.Equal(Now.AddMinutes(20) - option.WalkToStop - TimeSpan.FromMinutes(1), option.LeaveBy);
    }

    [Fact]
    public async Task LineThatNeverReachesTheDestinationRegion_IsFilteredOut()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] =
        [
            Departure(ride: 10, minutes: 10, sequence: 2, line: "167"),
            Departure(ride: 11, minutes: 12, sequence: 2, line: "4")
        ];
        // Only ride 11 calls near the destination.
        f.Schedule.DestinationRows = [Row(ride: 11, stop: 2, minutes: 30, sequence: 6)];

        var plan = await f.Planner.PlanAsync(Request());

        Assert.Equal(["4"], plan.Options.Select(option => option.Line).ToArray());
    }

    [Fact]
    public async Task RideThatCallsAtTheDestinationBeforeTheOrigin_IsWrongDirection()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = [Departure(ride: 10, minutes: 10, sequence: 7)];
        f.Schedule.DestinationRows = [Row(ride: 10, stop: 2, minutes: 5, sequence: 3)];

        var plan = await f.Planner.PlanAsync(Request());

        Assert.Equal(TripPlanStatus.NoOptions, plan.Status);
    }

    [Fact]
    public async Task LiveArrival_ReplacesTheScheduledDepartureAndShiftsTheArrival()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = [Departure(ride: 10, minutes: 10, sequence: 2, line: "4")];
        f.Schedule.DestinationRows = [Row(ride: 10, stop: 2, minutes: 25, sequence: 5)];
        f.Aggregator.Arrivals["100"] = [new Arrival("100", "4", "Dan", "X", Now.AddMinutes(12), IsLive: true, ArrivalSource.Curlbus, Now.AddSeconds(-20))];

        var option = Assert.Single((await f.Planner.PlanAsync(Request())).Options);

        Assert.True(option.IsLive);
        Assert.Equal(ArrivalSource.Curlbus, option.Source);
        Assert.Equal(Now.AddMinutes(12), option.BusDeparture);
        // Scheduled ride time is 15 minutes, so arrival moves with the delay.
        Assert.Equal(Now.AddMinutes(27), option.ArriveAt);
    }

    [Fact]
    public async Task LiveArrivalOfAnotherLine_IsNotUsed()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = [Departure(ride: 10, minutes: 10, sequence: 2, line: "4")];
        f.Schedule.DestinationRows = [Row(ride: 10, stop: 2, minutes: 25, sequence: 5)];
        f.Aggregator.Arrivals["100"] = [new Arrival("100", "9", "Dan", "X", Now.AddMinutes(11), true, ArrivalSource.Curlbus, Now)];

        var option = Assert.Single((await f.Planner.PlanAsync(Request())).Options);

        Assert.False(option.IsLive);
    }

    [Fact]
    public async Task BusThatCannotBeCaughtAfterWalking_IsDropped()
    {
        var f = new Fixture(new Stop("100", "Origin stop", null, 32.1060, 34.8000, Id: 1), StopB);
        // About 670 m away is over 10 minutes of walking, but the bus leaves in 3.
        f.Schedule.Departures[1] = [Departure(ride: 10, minutes: 3, sequence: 2), Departure(ride: 11, minutes: 30, sequence: 2)];
        f.Schedule.DestinationRows = [Row(10, 2, 15, 5), Row(11, 2, 50, 5)];

        var plan = await f.Planner.PlanAsync(Request());

        Assert.Equal([11L], plan.Options.Select(option => option.RideId).ToArray());
    }

    [Fact]
    public async Task DepartAt_OnlyOffersBusesYouCanCatchAfterThatTime()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = [Departure(10, 10, 2), Departure(11, 40, 2)];
        f.Schedule.DestinationRows = [Row(10, 2, 25, 5), Row(11, 2, 55, 5)];

        var plan = await f.Planner.PlanAsync(Request(TripTimeMode.DepartAt, Now.AddMinutes(30)));

        Assert.Equal([11L], plan.Options.Select(option => option.RideId).ToArray());
        Assert.Equal(0, f.Aggregator.Calls);
    }

    [Fact]
    public async Task ArriveBy_KeepsOnlyOptionsThatArriveInTimeLatestDepartureFirst()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = [Departure(10, 10, 2), Departure(11, 20, 2), Departure(12, 40, 2)];
        f.Schedule.DestinationRows = [Row(10, 2, 25, 5), Row(11, 2, 35, 5), Row(12, 2, 55, 5)];

        var plan = await f.Planner.PlanAsync(Request(TripTimeMode.ArriveBy, Now.AddMinutes(40)));

        Assert.Equal([11L, 10L], plan.Options.Select(option => option.RideId).ToArray());
        Assert.All(plan.Options, option => Assert.True(option.ArriveAt <= Now.AddMinutes(40)));
    }

    [Fact]
    public async Task PreferredLine_RestrictsTheResults()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = [Departure(10, 10, 2, line: "4"), Departure(11, 12, 2, line: "9")];
        f.Schedule.DestinationRows = [Row(10, 2, 25, 5), Row(11, 2, 28, 5)];

        var plan = await f.Planner.PlanAsync(Request() with { PreferredLine = " 9 " });

        Assert.Equal(["9"], plan.Options.Select(option => option.Line).ToArray());
    }

    [Fact]
    public async Task OptionsAreSortedByArrivalAndOneIsKeptPerRide()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = [Departure(10, 20, 2, line: "4"), Departure(11, 10, 2, line: "9")];
        f.Schedule.DestinationRows = [Row(10, 2, 30, 5), Row(11, 2, 35, 5)];

        var plan = await f.Planner.PlanAsync(Request());

        Assert.Equal(["4", "9"], plan.Options.Select(option => option.Line).ToArray());
    }

    [Fact]
    public async Task NoStopsNearOrigin_IsReported()
    {
        var f = new Fixture(StopFar, StopB);

        var plan = await f.Planner.PlanAsync(Request());

        Assert.Equal(TripPlanStatus.NoStopsNearOrigin, plan.Status);
        Assert.Empty(plan.Options);
    }

    [Fact]
    public async Task NoStopsNearDestination_IsReported()
    {
        var f = new Fixture(StopA, StopFar);

        var plan = await f.Planner.PlanAsync(Request());

        Assert.Equal(TripPlanStatus.NoStopsNearDestination, plan.Status);
    }

    [Fact]
    public async Task ArriveBy_SearchesTheHoursBeforeTheTarget_NotFromNow()
    {
        var f = new Fixture();
        var target = Now.AddHours(8);
        f.Schedule.Departures[1] = [Departure(10, 15, 2), Departure(11, (8 * 60) - 40, 2)];
        f.Schedule.DestinationRows = [Row(10, 2, 30, 5), Row(11, 2, (8 * 60) - 20, 5)];

        var plan = await f.Planner.PlanAsync(Request(TripTimeMode.ArriveBy, target));

        Assert.All(f.Schedule.DepartureWindows, window => Assert.True(window.From >= target - TimeSpan.FromHours(3)));
        Assert.Equal([11L], plan.Options.Select(option => option.RideId).ToArray());
    }

    [Fact]
    public async Task Departures_AreFetchedOnlyForTheSearchWindow()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = [Departure(10, 10, 2)];
        f.Schedule.DestinationRows = [Row(10, 2, 25, 5)];

        await f.Planner.PlanAsync(Request());

        Assert.All(f.Schedule.DepartureWindows, window => Assert.Equal(TimeSpan.FromMinutes(90), window.To - window.From));
    }

    [Fact]
    public async Task BusyOrigin_EveryCandidateRideIsChecked_InBatchesOf80()
    {
        // Stop A has 95 rides in the wrong direction; stop A2 nearby has the one ride that reaches the destination.
        var stopA2 = new Stop("101", "Other side", "City", 32.1003, 34.8000, Id: 4);
        var f = new Fixture(StopA, StopB, stopA2);
        f.Schedule.Departures[1] = Enumerable.Range(1, 95).Select(i => Departure(i, 5 + (i % 60), 2)).ToArray();
        f.Schedule.Departures[4] = [new RideStop(500, 4, 3, Now.AddMinutes(12), "9", "Dan", "A<->B-City-1")];
        f.Schedule.DestinationRows = [Row(500, 2, 30, 8)];

        var plan = await f.Planner.PlanAsync(Request());

        Assert.Equal([500L], plan.Options.Select(option => option.RideId).ToArray());
        Assert.All(f.Schedule.RideIdBatchSizes, size => Assert.InRange(size, 1, 80));
        Assert.Equal(96, f.Schedule.RideIdBatchSizes.Sum());
        Assert.True(f.Schedule.RideIdBatchSizes.Count >= 2);
    }

    [Fact]
    public async Task WatchedRide_PastItsScheduledTimeButLiveLater_IsStillReturnedWithTheLiveTime()
    {
        var f = new Fixture();
        // Scheduled 5 minutes ago, but the live feed says it is 7 minutes away.
        f.Schedule.Departures[1] = [Departure(10, -5, 2, line: "4")];
        f.Schedule.DestinationRows = [Row(10, 2, 10, 5)];
        f.Aggregator.Arrivals["100"] = [new Arrival("100", "4", "Dan", "X", Now.AddMinutes(7), IsLive: true, ArrivalSource.Curlbus, Now)];

        var unwatched = await f.Planner.PlanAsync(Request());
        var watched = await f.Planner.PlanAsync(Request() with { WatchRideId = 10 });

        Assert.Empty(unwatched.Options);
        var option = Assert.Single(watched.Options);
        Assert.True(option.IsLive);
        Assert.Equal(Now.AddMinutes(7), option.BusDeparture);
    }

    [Fact]
    public async Task WatchedRide_GetsLiveDataEvenForDepartAtRequests()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = [Departure(10, 40, 2, line: "4")];
        f.Schedule.DestinationRows = [Row(10, 2, 55, 5)];
        f.Aggregator.Arrivals["100"] = [new Arrival("100", "4", "Dan", "X", Now.AddMinutes(44), IsLive: true, ArrivalSource.Curlbus, Now)];

        var plan = await f.Planner.PlanAsync(Request(TripTimeMode.DepartAt, Now.AddMinutes(30)) with { WatchRideId = 10 });

        Assert.True(Assert.Single(plan.Options).IsLive);
    }

    [Fact]
    public async Task WatchedRide_IsKeptEvenBeyondTheTopEightOptions()
    {
        var f = new Fixture();
        f.Schedule.Departures[1] = Enumerable.Range(1, 12).Select(i => Departure(i, 5 + i, 2)).ToArray();
        f.Schedule.DestinationRows = Enumerable.Range(1, 12).Select(i => Row(i, 2, 20 + i, 5)).ToArray();

        var plan = await f.Planner.PlanAsync(Request() with { WatchRideId = 12 });

        Assert.Contains(plan.Options, option => option.RideId == 12);
        Assert.Equal(9, plan.Options.Count);
    }

    [Fact]
    public void Walking_Estimate_UsesDetourFactorAndSpeed()
    {
        Assert.Equal(TimeSpan.FromSeconds(400), Walking.Estimate(400));
        Assert.Equal(TimeSpan.Zero, Walking.Estimate(-5));
    }

    [Fact]
    public void RouteEndpoints_ExtractOriginAndDestinationFromARealRouteName()
    {
        const string name = "לח''י/מבצע קדש-בני ברק<->קהילות יעקב/רבי יוסף צבי רימון-בני ברק-10";

        Assert.Equal("לח''י/מבצע קדש", RouteEndpoints.Origin(name));
        Assert.Equal("קהילות יעקב/רבי יוסף צבי רימון", RouteEndpoints.Destination(name));
        Assert.Null(RouteEndpoints.Origin("no separator"));
    }

    [Fact]
    public void RideScheduleParser_ReadsRealStrideResponses()
    {
        var departures = StrideRideScheduleSource.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stride-origin-departures.json")));
        var destination = StrideRideScheduleSource.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stride-dest-ride-stops.json")));

        Assert.NotEmpty(departures);
        Assert.All(departures, row => Assert.Equal(6, row.Sequence));
        Assert.All(departures, row => Assert.False(string.IsNullOrEmpty(row.Line)));
        Assert.NotEmpty(destination);
        // The same rides continue to later sequence numbers at the destination stops.
        var rideIds = departures.Select(row => row.RideId).ToHashSet();
        Assert.All(destination, row => Assert.Contains(row.RideId, rideIds));
        Assert.All(destination, row => Assert.True(row.Sequence > 6));
    }

    [Fact]
    public void RideScheduleParser_SkipsRowsMissingRequiredFields()
    {
        const string json = """[{"gtfs_ride_id":1},{"gtfs_ride_id":2,"gtfs_stop_id":3,"stop_sequence":4,"arrival_time":"nope"},{"gtfs_ride_id":5,"gtfs_stop_id":6,"stop_sequence":7,"arrival_time":"2026-10-20T10:00:00+00:00"}]""";

        var row = Assert.Single(StrideRideScheduleSource.Parse(System.Text.Encoding.UTF8.GetBytes(json)));

        Assert.Equal(5, row.RideId);
    }

    private static TripRequest Request(TripTimeMode mode = TripTimeMode.Now, DateTimeOffset? target = null) =>
        new(32.1000, 34.8000, 32.1100, 34.8000, mode, target);

    private static RideStop Departure(long ride, int minutes, int sequence, string line = "4") =>
        new(ride, 1, sequence, Now.AddMinutes(minutes), line, "Dan", "Origin-City<->Destination-City-1");

    private static RideStop Row(long ride, long stop, int minutes, int sequence) =>
        new(ride, stop, sequence, Now.AddMinutes(minutes), string.Empty, null, null);

    private sealed class Fixture
    {
        public Fixture(Stop? origin = null, Stop? destination = null, params Stop[] extra)
        {
            Schedule = new FakeRideSchedule();
            Aggregator = new FakeAggregator();
            Planner = new TripPlannerService(
                new FakeCatalog([origin ?? StopA, destination ?? StopB, .. extra]),
                Schedule,
                Aggregator,
                new FixedClock(Now),
                NullLogger<TripPlannerService>.Instance);
        }

        public FakeRideSchedule Schedule { get; }
        public FakeAggregator Aggregator { get; }
        public TripPlannerService Planner { get; }
    }

    /// <summary>
    /// Behaves like the Stride source: honours the time window, returns at most 100 departures in the requested order,
    /// and rejects more than 80 ride ids per query.
    /// </summary>
    internal sealed class FakeRideSchedule : IRideScheduleSource
    {
        public Dictionary<long, IReadOnlyList<RideStop>> Departures { get; } = new();
        public IReadOnlyList<RideStop> DestinationRows { get; set; } = [];
        public List<(DateTimeOffset From, DateTimeOffset To)> DepartureWindows { get; } = [];
        public List<long> DepartureStopIds { get; } = [];
        public List<int> RideIdBatchSizes { get; } = [];

        public Task<IReadOnlyList<RideStop>> GetStopDeparturesAsync(
            long stopId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default, bool latestFirst = false)
        {
            DepartureWindows.Add((from, to));
            DepartureStopIds.Add(stopId);
            var inWindow = (Departures.GetValueOrDefault(stopId) ?? []).Where(row => row.Time >= from && row.Time <= to);
            var ordered = latestFirst ? inWindow.OrderByDescending(row => row.Time) : inWindow.OrderBy(row => row.Time);
            return Task.FromResult<IReadOnlyList<RideStop>>(ordered.Take(100).ToArray());
        }

        public Task<IReadOnlyList<RideStop>> GetRideStopsAsync(long rideId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RideStop>>(RideStops.Where(row => row.RideId == rideId).ToArray());

        public List<RideStop> RideStops { get; } = [];

        public Task<IReadOnlyList<RideStop>> GetRideStopsAtAsync(
            IReadOnlyCollection<long> rideIds,
            IReadOnlyCollection<long> stopIds,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default)
        {
            RideIdBatchSizes.Add(rideIds.Count);
            if (rideIds.Count > 80)
            {
                throw new InvalidOperationException("The Stride source only accepts 80 ride ids per query.");
            }

            return Task.FromResult<IReadOnlyList<RideStop>>(DestinationRows.Where(row => rideIds.Contains(row.RideId) && stopIds.Contains(row.StopId)).ToArray());
        }
    }

    internal sealed class FakeAggregator : ILiveDataAggregator
    {
        public Dictionary<string, Arrival[]> Arrivals { get; } = new();
        public int Calls { get; private set; }

        public Task<AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AggregatedArrivals(Arrivals.GetValueOrDefault(stopCode) ?? [], new Dictionary<ArrivalSource, ProviderStatus>()));
        }
    }
}
