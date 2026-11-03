using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using BusPulse.Services.LiveData;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>
/// Second review: stop ids from an earlier day's list, a departed watched ride taking the next bus's live time, and
/// arrive-by losing the departures closest to the target at a busy stop.
/// </summary>
public sealed class StopIdsAndLiveMatchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop StopA = new("100", "Origin stop", "Bnei Brak", 32.1000, 34.8000, Id: 1);
    private static readonly Stop StopB = new("200", "Destination stop", "Tel Aviv", 32.1100, 34.8000, Id: 2);

    [Fact]
    public async Task Planner_WithAnEarlierDaysStopList_QueriesTodaysStopIds()
    {
        var catalog = new RemappingCatalog([StopA, StopB], new() { ["100"] = 11, ["200"] = 22 });
        var schedule = new TripPlannerServiceTests.FakeRideSchedule();
        schedule.Departures[11] = [Departure(10, 10)];
        schedule.DestinationRows = [Row(10, stop: 22, minutes: 25)];

        var plan = await Planner(catalog, schedule).PlanAsync(Request());

        Assert.Equal(10, Assert.Single(plan.Options).RideId);
        Assert.All(schedule.DepartureStopIds, id => Assert.Equal(11, id));
    }

    [Fact]
    public async Task Planner_OriginStopWithNoServiceToday_IsLeftOut()
    {
        var catalog = new RemappingCatalog([StopA, StopB], new() { ["200"] = 22 });

        var plan = await Planner(catalog, new TripPlannerServiceTests.FakeRideSchedule()).PlanAsync(Request());

        Assert.Equal(TripPlanStatus.NoStopsNearOrigin, plan.Status);
    }

    [Fact]
    public async Task StopCatalog_ServingAnEarlierDaysCache_LooksUpTodaysIdByCodeOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bp-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stride-stops-sample.json"));
            var clock = new FixedClock(DateTimeOffset.Parse("2026-10-20T12:00:00+00:00"));
            await new StrideStopCatalog(new Factory(new RoutingHandler(fixture, "[]")), clock, NullLogger<StrideStopCatalog>.Instance, directory).GetStopsAsync();

            // Two weeks later today's download fails, so the old list (with old ids) is served.
            clock.UtcNow = DateTimeOffset.Parse("2026-11-03T12:00:00+00:00");
            const string lookup = """[{"id":99,"date":"2026-11-03","code":3490,"lat":32.104812,"lon":34.846465,"name":"תחנת שיטור/איילת השחר","city":"בני ברק"}]""";
            var handler = new RoutingHandler(bulk: null, lookup);
            var catalog = new StrideStopCatalog(new Factory(handler), clock, NullLogger<StrideStopCatalog>.Instance, directory);
            var stops = await catalog.GetStopsAsync();
            await catalog.WhenRefreshedAsync();
            var old = stops.Single(stop => stop.Code == "3490");

            var first = await catalog.WithCurrentIdsAsync([old]);
            var second = await catalog.WithCurrentIdsAsync([old]);

            Assert.Equal(49480910, old.Id);
            Assert.Equal(99, Assert.Single(first).Id);
            Assert.Equal(99, Assert.Single(second).Id);
            Assert.Equal(1, handler.Lookups);
            Assert.Contains("date_from=2026-11-03", handler.LastLookup);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StopCatalog_TodaysList_KeepsItsIdsWithoutAnyLookup()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bp-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stride-stops-sample.json"));
            var handler = new RoutingHandler(fixture, "[]");
            var catalog = new StrideStopCatalog(new Factory(handler), new FixedClock(Now), NullLogger<StrideStopCatalog>.Instance, directory);
            var stops = await catalog.GetStopsAsync();

            var current = await catalog.WithCurrentIdsAsync(stops);

            Assert.Same(stops, current);
            Assert.Equal(0, handler.Lookups);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ArriveBy_AtABusyStop_StillFindsTheDeparturesJustBeforeTheTarget()
    {
        // 150 departures in the three hours before the target; the source returns only 100 rows per query.
        var schedule = new TripPlannerServiceTests.FakeRideSchedule();
        schedule.Departures[1] = Enumerable.Range(1, 150).Select(i => Departure(i, i)).ToArray();
        schedule.DestinationRows = [Row(140, stop: 2, minutes: 160)];

        var plan = await Planner(new FakeCatalog(StopA, StopB), schedule).PlanAsync(Request(TripTimeMode.ArriveBy, Now.AddHours(3)));

        Assert.Equal(140, Assert.Single(plan.Options).RideId);
    }

    [Fact]
    public async Task WatchedRideThatAlreadyLeft_DoesNotTakeTheNextBusesLiveTime()
    {
        var schedule = new TripPlannerServiceTests.FakeRideSchedule();
        schedule.Departures[1] = [Departure(10, -5, "J10"), Departure(11, 6, "J11")];
        schedule.DestinationRows = [Row(10, stop: 2, minutes: 10), Row(11, stop: 2, minutes: 21)];
        var aggregator = new TripPlannerServiceTests.FakeAggregator();
        // Only the next bus is still coming; it is 2 minutes early.
        aggregator.Arrivals["100"] = [new Arrival("100", "4", "Dan", "X", Now.AddMinutes(4), IsLive: true, ArrivalSource.Curlbus, Now, TripRef: "J11")];

        var plan = await Planner(new FakeCatalog(StopA, StopB), schedule, aggregator).PlanAsync(Request() with { WatchRideId = 10 });

        var watched = plan.Options.Single(option => option.RideId == 10);
        var next = plan.Options.Single(option => option.RideId == 11);
        Assert.False(watched.IsLive);
        Assert.Equal(Now.AddMinutes(-5), watched.BusDeparture);
        Assert.True(next.IsLive);
        Assert.Equal(Now.AddMinutes(4), next.BusDeparture);
    }

    [Fact]
    public async Task LiveArrival_IsMatchedByTripEvenWhenAnotherBusIsCloserInTime()
    {
        var schedule = new TripPlannerServiceTests.FakeRideSchedule();
        schedule.Departures[1] = [Departure(10, 10, "J10")];
        schedule.DestinationRows = [Row(10, stop: 2, minutes: 25)];
        var aggregator = new TripPlannerServiceTests.FakeAggregator();
        aggregator.Arrivals["100"] =
        [
            new Arrival("100", "4", "Dan", "X", Now.AddMinutes(9), IsLive: true, ArrivalSource.Curlbus, Now, TripRef: "J09"),
            new Arrival("100", "4", "Dan", "X", Now.AddMinutes(18), IsLive: true, ArrivalSource.Curlbus, Now, TripRef: "J10")
        ];

        var option = Assert.Single((await Planner(new FakeCatalog(StopA, StopB), schedule, aggregator).PlanAsync(Request())).Options);

        Assert.Equal(Now.AddMinutes(18), option.BusDeparture);
    }

    [Fact]
    public async Task Curlbus_ReadsTheTripIdOfEachVisit()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "curlbus-stop-3490.json"), Encoding.UTF8);
        var provider = new CurlbusArrivalsProvider(
            new Factory(new RoutingHandler(body, body)), new FixedClock(DateTimeOffset.Parse("2026-10-20T19:26:00+03:00")), NullLogger<CurlbusArrivalsProvider>.Instance);

        var result = await provider.GetArrivalsAsync("3490");

        Assert.Equal("56010199_201026", result.Arrivals.First(arrival => arrival.LineName == "4").TripRef);
    }

    [Fact]
    public void StrideParser_ReadsTheJourneyRefOfEachRide()
    {
        var rows = StrideRideScheduleSource.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stride-origin-departures.json")));

        Assert.Equal("53264001_201026", rows[0].JourneyRef);
        Assert.All(rows, row => Assert.False(string.IsNullOrEmpty(row.JourneyRef)));
    }

    private static TripPlannerService Planner(IStopCatalog catalog, IRideScheduleSource schedule, ILiveDataAggregator? aggregator = null) =>
        new(catalog, schedule, aggregator ?? new TripPlannerServiceTests.FakeAggregator(), new FixedClock(Now), NullLogger<TripPlannerService>.Instance);

    private static TripRequest Request(TripTimeMode mode = TripTimeMode.Now, DateTimeOffset? target = null) =>
        new(32.1000, 34.8000, 32.1100, 34.8000, mode, target);

    private static RideStop Departure(long ride, int minutes, string? journeyRef = null) =>
        new(ride, 1, 2, Now.AddMinutes(minutes), "4", "Dan", "Origin-City<->Destination-City-1", JourneyRef: journeyRef);

    private static RideStop Row(long ride, long stop, int minutes) =>
        new(ride, stop, 5, Now.AddMinutes(minutes), string.Empty, null, null);

    /// <summary>A list from an earlier day: today's ids differ, and a stop missing from the map has no service today.</summary>
    private sealed class RemappingCatalog(IReadOnlyList<Stop> stops, Dictionary<string, long> todaysIds) : IStopCatalog
    {
        public IReadOnlyList<Stop>? Loaded => stops;

        public Task<IReadOnlyList<Stop>> GetStopsAsync(CancellationToken cancellationToken = default) => Task.FromResult(stops);

        public Task<IReadOnlyList<Stop>> WithCurrentIdsAsync(IReadOnlyList<Stop> requested, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Stop>>(requested
                .Where(stop => todaysIds.ContainsKey(stop.Code))
                .Select(stop => stop with { Id = todaysIds[stop.Code] })
                .ToArray());
    }

    /// <summary>Serves the bulk stop list (or 503 when null) and answers single-stop lookups by code.</summary>
    private sealed class RoutingHandler(string? bulk, string lookup) : HttpMessageHandler
    {
        public int Lookups { get; private set; }
        public string LastLookup { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("code=", StringComparison.Ordinal))
            {
                Lookups++;
                LastLookup = uri;
                return Task.FromResult(Json(lookup));
            }

            return Task.FromResult(bulk is null ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(bulk));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
