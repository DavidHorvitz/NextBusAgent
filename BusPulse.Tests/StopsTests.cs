using System.IO;
using System.Net;
using System.Net.Http;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.LiveData;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class GeoTests
{
    [Fact]
    public void DistanceMeters_BetweenTwoRealStops_IsAboutHalfAKilometer()
    {
        var meters = Geo.DistanceMeters(32.104812, 34.846465, 32.104174, 34.84156);

        Assert.InRange(meters, 440, 510);
    }

    [Fact]
    public void DistanceMeters_SamePoint_IsZero() =>
        Assert.Equal(0, Geo.DistanceMeters(32.1, 34.8, 32.1, 34.8), 3);

    [Theory]
    [InlineData(32.1, 34.8, true)]
    [InlineData(0, 0, false)]
    [InlineData(40.7, -74.0, false)]
    [InlineData(32.1, 38.0, false)]
    public void IsInIsrael_RejectsCoordinatesOutsideTheCountry(double lat, double lon, bool expected) =>
        Assert.Equal(expected, Geo.IsInIsrael(lat, lon));

    [Fact]
    public void FindNearby_SortsByDistance_AndAppliesLimitAndRadius()
    {
        var near = new Stop("1", "near", null, 32.1000, 34.8000);
        var mid = new Stop("2", "mid", null, 32.1020, 34.8000);
        var far = new Stop("3", "far", null, 32.2000, 34.8000);

        var result = Geo.FindNearby([far, mid, near], 32.1000, 34.8000, count: 2, maxMeters: 3000);

        Assert.Equal(["1", "2"], result.Select(n => n.Stop.Code).ToArray());
        Assert.Equal(0, result[0].DistanceMeters, 3);
    }
}

public sealed class StrideStopCatalogTests
{
    [Fact]
    public void ParseStops_RealFixture_ReturnsAllValidStops()
    {
        var stops = StrideStopCatalog.ParseStops(File.ReadAllBytes(FixturePath()));

        Assert.Equal(5, stops.Count);
        Assert.Contains(stops, stop => stop.Code == "3490" && stop.City == "בני ברק");
    }

    [Fact]
    public void ParseStops_SkipsRowsWithInvalidCoordinatesCodesOrNames()
    {
        const string json = """
            [{"code":1,"lat":0,"lon":0,"name":"nowhere"},
             {"code":2,"lat":32.1,"lon":34.8,"name":""},
             {"code":0,"lat":32.1,"lon":34.8,"name":"zero code"},
             {"code":"x","lat":32.1,"lon":34.8,"name":"bad code"},
             {"code":5,"lat":32.1,"lon":34.8,"name":"ok"}]
            """;

        var stop = Assert.Single(StrideStopCatalog.ParseStops(System.Text.Encoding.UTF8.GetBytes(json)));

        Assert.Equal("5", stop.Code);
    }

    [Fact]
    public async Task GetStops_DownloadsOnce_ThenServesTheDailyCacheFromDisk()
    {
        var directory = Path.Combine(Path.GetTempPath(), "buspulse-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FixedClock(DateTimeOffset.Parse("2026-10-20T16:00:00+00:00"));
            var firstHandler = new CountingHandler(File.ReadAllText(FixturePath()));
            var first = new StrideStopCatalog(new Factory(firstHandler), clock, NullLogger<StrideStopCatalog>.Instance, directory);

            Assert.Equal(5, (await first.GetStopsAsync()).Count);
            Assert.Equal(1, firstHandler.Calls);

            var secondHandler = new CountingHandler("[]");
            var second = new StrideStopCatalog(new Factory(secondHandler), clock, NullLogger<StrideStopCatalog>.Instance, directory);

            Assert.Equal(5, (await second.GetStopsAsync()).Count);
            Assert.Equal(0, secondHandler.Calls);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string FixturePath() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "stride-stops-sample.json");

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CountingHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}

public sealed class StopsViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T19:26:00+03:00");
    private static readonly Stop Near = new("3490", "Near stop", "Bnei Brak", 32.1048, 34.8464);
    private static readonly Stop Middle = new("3128", "Middle stop", "Bnei Brak", 32.1041, 34.8415);
    private static readonly Stop Far = new("4147", "Far stop", "Tel Aviv", 32.1279, 34.7924);

    [Fact]
    public async Task Load_WithLocation_ListsStopsNearestFirst_WithoutRawCoordinates()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.1048, 34.8464, 25);

        await f.Stops.LoadCommand.ExecuteAsync(null);

        // "Far stop" is about 5 km away, outside the 3 km radius.
        Assert.Equal(["3490", "3128"], f.Stops.Stops.Select(s => s.Code).ToArray());
        Assert.True(f.Stops.Stops[0].DistanceMeters < f.Stops.Stops[1].DistanceMeters);
        Assert.Equal("0 m", f.Stops.Stops[0].DistanceText);
        Assert.DoesNotContain("32.10", f.Stops.LocationText);
        Assert.Equal(string.Empty, f.Stops.StatusText);
    }

    [Theory]
    [InlineData(LocationStatus.PermissionDenied, "LocationPermissionDenied")]
    [InlineData(LocationStatus.Unavailable, "LocationUnavailable")]
    [InlineData(LocationStatus.OutsideCoverage, "LocationOutsideCoverage")]
    public async Task Load_WhenLocationFails_ShowsAnActionableMessageAndNoStops(LocationStatus status, string message)
    {
        var f = new Fixture();
        f.Location.Result = LocationResult.Failed(status);

        await f.Stops.LoadCommand.ExecuteAsync(null);

        Assert.Empty(f.Stops.Stops);
        Assert.Equal(message, f.Stops.StatusText);
    }

    [Fact]
    public async Task Load_WithSavedPlaceAsAnchor_DoesNotAskForLocation()
    {
        var f = new Fixture();
        f.Places.Items.Add(new SavedPlace { Name = "Home", StopCode = "4147" });

        f.Stops.SelectedAnchor = f.Places.Items[0];
        await f.Stops.LoadCommand.ExecuteAsync(null);

        Assert.Equal(0, f.Location.Calls);
        Assert.Equal("4147", f.Stops.Stops[0].Code);
    }

    [Fact]
    public async Task Load_WhenAnchorStopIsMissing_ReportsIt()
    {
        var f = new Fixture();
        f.Places.Items.Add(new SavedPlace { Name = "Gone", StopCode = "99999" });
        f.Stops.SelectedAnchor = f.Places.Items[0];

        await f.Stops.LoadCommand.ExecuteAsync(null);

        Assert.Empty(f.Stops.Stops);
        Assert.Equal("PlaceNotFound", f.Stops.StatusText);
    }

    [Fact]
    public async Task Load_WhenCatalogFails_ShowsRetryMessage()
    {
        var f = new Fixture();
        f.Catalog.Throw = new HttpRequestException("down");

        await f.Stops.LoadCommand.ExecuteAsync(null);

        Assert.Equal("StopsLoadFailed", f.Stops.StatusText);
        Assert.False(f.Stops.IsLoading);
    }

    [Fact]
    public async Task Reload_WithSameStops_KeepsTheSameItemsInPlace()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.1048, 34.8464, 25);
        await f.Stops.LoadCommand.ExecuteAsync(null);
        var before = f.Stops.Stops.ToArray();

        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.1049, 34.8465, 25);
        await f.Stops.LoadCommand.ExecuteAsync(null);

        Assert.Equal(before, f.Stops.Stops.ToArray());
    }

    [Fact]
    public async Task ToggleStop_LoadsArrivalsWithLiveAndScheduledBadges()
    {
        var f = await LoadedAsync();
        f.Aggregator.Result = Result(Live("4", minutes: 5), Scheduled("9", minutes: 12));

        await f.Stops.ToggleStopCommand.ExecuteAsync(f.Stops.Stops[0]);

        var rows = f.Stops.Stops[0].Arrivals;
        Assert.True(f.Stops.Stops[0].IsExpanded);
        Assert.Equal(["4", "9"], rows.Select(r => r.LineName).ToArray());
        Assert.Equal(["LiveBadge", "ScheduledBadge"], rows.Select(r => r.BadgeText).ToArray());
        Assert.Equal("5m", rows[0].EtaText);
        Assert.Equal(string.Empty, f.Stops.Stops[0].ArrivalsStatus);
        Assert.Contains("ProviderCurlbus", rows[0].Tooltip);
    }

    [Fact]
    public async Task ToggleStop_WithOnlyScheduledData_SaysThereIsNoLiveData()
    {
        var f = await LoadedAsync();
        f.Aggregator.Result = Result(Scheduled("9", minutes: 12));

        await f.Stops.ToggleStopCommand.ExecuteAsync(f.Stops.Stops[0]);

        Assert.Equal("NoLiveData", f.Stops.Stops[0].ArrivalsStatus);
    }

    [Fact]
    public async Task RefreshExpanded_ReusesRowsAndTrimsTheTail()
    {
        var f = await LoadedAsync();
        f.Aggregator.Result = Result(Live("4", 5), Live("9", 8), Live("3", 11));
        await f.Stops.ToggleStopCommand.ExecuteAsync(f.Stops.Stops[0]);
        var firstRow = f.Stops.Stops[0].Arrivals[0];

        f.Aggregator.Result = Result(Live("4", 4));
        await f.Stops.RefreshExpandedAsync();

        var rows = f.Stops.Stops[0].Arrivals;
        Assert.Single(rows);
        Assert.Same(firstRow, rows[0]);
        Assert.Equal("4m", rows[0].EtaText);
    }

    [Fact]
    public async Task RefreshExpanded_IgnoresCollapsedStops()
    {
        var f = await LoadedAsync();
        f.Aggregator.Calls = 0;

        await f.Stops.RefreshExpandedAsync();

        Assert.Equal(0, f.Aggregator.Calls);
    }

    [Fact]
    public async Task Tick_CountsDownWithoutNetworkAndGreysOutStaleLiveData()
    {
        var f = await LoadedAsync();
        f.Aggregator.Result = Result(Live("4", minutes: 5, observedAgoSeconds: 30));
        await f.Stops.ToggleStopCommand.ExecuteAsync(f.Stops.Stops[0]);
        var calls = f.Aggregator.Calls;
        var row = f.Stops.Stops[0].Arrivals[0];
        Assert.False(row.IsStale);

        f.Clock.UtcNow = Now.AddMinutes(4).AddSeconds(30);
        f.Stops.Tick();

        Assert.Equal("1m", row.EtaText);
        Assert.True(row.IsStale);
        Assert.Contains("300s ago", row.Tooltip);
        Assert.Equal(calls, f.Aggregator.Calls);

        f.Clock.UtcNow = Now.AddMinutes(6);
        f.Stops.Tick();

        Assert.Equal("now", row.EtaText);
    }

    [Fact]
    public async Task PinningAStop_MovesItToTheTopAndPersists()
    {
        var f = await LoadedAsync();
        var settings = f.Settings;
        var farthest = f.Stops.Stops.Last();

        await f.Stops.TogglePinStopCommand.ExecuteAsync(farthest);

        Assert.Equal(farthest.Code, f.Stops.Stops[0].Code);
        Assert.True(f.Stops.Stops[0].IsFavorite);
        Assert.Contains(farthest.Code, settings.Config.FavoriteStopCodes);

        await f.Stops.TogglePinStopCommand.ExecuteAsync(f.Stops.Stops[0]);

        Assert.Empty(settings.Config.FavoriteStopCodes);
        Assert.False(f.Stops.Stops.Any(stop => stop.IsFavorite));
    }

    [Fact]
    public async Task PinnedStops_StayOnTopAfterAReload()
    {
        var f = await LoadedAsync();
        var farthest = f.Stops.Stops.Last();
        await f.Stops.TogglePinStopCommand.ExecuteAsync(farthest);

        await f.Stops.LoadCommand.ExecuteAsync(null);

        Assert.Equal(farthest.Code, f.Stops.Stops[0].Code);
    }

    private static async Task<Fixture> LoadedAsync()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.1048, 34.8464, 25);
        await f.Stops.LoadCommand.ExecuteAsync(null);
        return f;
    }

    private static AggregatedArrivals Result(params Arrival[] arrivals) =>
        new(arrivals, new Dictionary<ArrivalSource, ProviderStatus>());

    private static Arrival Live(string line, int minutes, int observedAgoSeconds = 10) =>
        new("3490", line, "Dan", "Dest", Now.AddMinutes(minutes), IsLive: true, ArrivalSource.Curlbus, Now.AddSeconds(-observedAgoSeconds));

    private static Arrival Scheduled(string line, int minutes) =>
        new("3490", line, "Dan", "Dest", Now.AddMinutes(minutes), IsLive: false, ArrivalSource.Schedule, Now);

    private sealed class Fixture
    {
        public Fixture()
        {
            Clock = new FixedClock(Now);
            Location = new FakeLocation();
            Catalog = new FakeCatalog(Far, Near, Middle);
            Aggregator = new CountingAggregator();
            var settings = new MemorySettings();
            Settings = settings;
            Places = new SavedPlaceCollection(settings);
            Stops = new StopsViewModel(
                settings, Location, Catalog, Aggregator, Places, new TemplateLocalizer(), Clock, new ImmediateDispatcher(),
                NullLogger<StopsViewModel>.Instance);
        }

        public MemorySettings Settings { get; }
        public FixedClock Clock { get; }
        public FakeLocation Location { get; }
        public FakeCatalog Catalog { get; }
        public CountingAggregator Aggregator { get; }
        public SavedPlaceCollection Places { get; }
        public StopsViewModel Stops { get; }
    }

    private sealed class CountingAggregator : ILiveDataAggregator
    {
        public int Calls { get; set; }
        public AggregatedArrivals Result { get; set; } = new([], new Dictionary<ArrivalSource, ProviderStatus>());

        public Task<AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }
}
