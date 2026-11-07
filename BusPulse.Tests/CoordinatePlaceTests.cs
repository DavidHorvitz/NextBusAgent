using System.IO;
using System.Text.Json;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>Saved places that have coordinates and no stop code.</summary>
public sealed class CoordinatePlaceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop Here = new("100", "Here stop", "City", 32.1000, 34.8000, Id: 1);
    private static readonly Stop There = new("200", "There stop", "City", 32.1100, 34.8000, Id: 2);
    private static readonly Stop NoId = new("300", "No id stop", "City", 32.1200, 34.8000);

    private static SavedPlace CoordinatePlace(string name = "Cafe", double lat = 32.10005, double lon = 34.80005) =>
        new() { Name = name, Latitude = lat, Longitude = lon, Address = "1 Main St" };

    // --- validation

    [Fact]
    public void Validate_NeitherStopCodeNorCoordinates_IsInvalid() =>
        Assert.Throws<InvalidDataException>(() => new SavedPlace { Name = "Nowhere" }.Validate());

    [Fact]
    public void Validate_CoordinatesOutsideIsrael_AreInvalid() =>
        Assert.Throws<InvalidDataException>(() => CoordinatePlace(lat: 48.85, lon: 2.35).Validate());

    [Fact]
    public void Validate_OnlyOneCoordinate_IsInvalid() =>
        Assert.Throws<InvalidDataException>(() => new SavedPlace { Name = "Half", StopCode = "100", Latitude = 32.1 }.Validate());

    [Fact]
    public void Validate_CoordinatesOnly_IsValid() => CoordinatePlace().Validate();

    [Fact]
    public void Validate_StopCodeAndCoordinates_IsValid() =>
        new SavedPlace { Name = "Both", StopCode = "100", Latitude = 32.1, Longitude = 34.8 }.Validate();

    [Fact]
    public void Validate_StopCodeOnly_StillValid() => new SavedPlace { Name = "Old", StopCode = "100" }.Validate();

    [Fact]
    public void Validate_AddressTooLong_IsInvalid()
    {
        var place = CoordinatePlace();
        place.Address = new string('a', 201);

        Assert.Throws<InvalidDataException>(place.Validate);
    }

    [Fact]
    public void DisplayDetail_PrefersAddressThenStopCode()
    {
        Assert.Equal("1 Main St", CoordinatePlace().DisplayDetail);
        Assert.Equal("100", new SavedPlace { Name = "Old", StopCode = "100" }.DisplayDetail);
        Assert.Equal(string.Empty, new SavedPlace { Name = "Bare", Latitude = 32.1, Longitude = 34.8 }.DisplayDetail);
    }

    // --- PlacePoint

    [Fact]
    public void PlacePoint_CoordinatesWinOverStopCode()
    {
        var place = new SavedPlace { Name = "Both", StopCode = "200", Latitude = 32.1, Longitude = 34.8 };

        var point = PlacePoint.Resolve(place, [Here, There]);

        Assert.Equal((32.1, 34.8), point);
    }

    [Fact]
    public void PlacePoint_StopCodeOnly_UsesTheCatalogStop()
    {
        var point = PlacePoint.Resolve(new SavedPlace { Name = "Old", StopCode = "200" }, [Here, There]);

        Assert.Equal((There.Latitude, There.Longitude), point);
    }

    [Fact]
    public void PlacePoint_UnknownStopCode_IsNull() =>
        Assert.Null(PlacePoint.Resolve(new SavedPlace { Name = "Gone", StopCode = "999" }, [Here]));

    [Fact]
    public void NearestStop_PicksTheClosestStopWithAnId()
    {
        var place = CoordinatePlace(lat: 32.1195, lon: 34.8);

        // NoId is closest but has no Stride id; There is 850 m away (too far), Here 2.1 km. Nothing in reach.
        Assert.Null(PlacePoint.NearestStop(place, [Here, There, NoId]));

        var closer = CoordinatePlace(lat: 32.1095, lon: 34.8);
        Assert.Equal(There, PlacePoint.NearestStop(closer, [Here, There, NoId]));
    }

    [Fact]
    public void NearestStop_BeyondWalkingDistance_IsNull() =>
        Assert.Null(PlacePoint.NearestStop(CoordinatePlace(lat: 32.5, lon: 34.9), [Here, There]));

    // --- trip planner

    private sealed class PlannerFixture
    {
        public PlannerFixture(params SavedPlace[] places)
        {
            var settings = new MemorySettings();
            var collection = new SavedPlaceCollection(settings);
            foreach (var place in places)
            {
                collection.Items.Add(place);
            }

            collection.SyncToConfig();
            Planner = new FakeTripPlanner();
            Vm = new TripPlannerViewModel(
                settings, collection, new TemplateLocalizer(), Planner, new FakeCatalog(Here, There),
                new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.09, 34.80) }, new FakeHistory(),
                new FakeReminder(), new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(),
                NullLogger<TripPlannerViewModel>.Instance, new FakeCalendar())
            {
                DebounceDelay = TimeSpan.Zero
            };
        }

        public FakeTripPlanner Planner { get; }
        public TripPlannerViewModel Vm { get; }
    }

    [Fact]
    public async Task Planner_CoordinateOnlyDestination_PlansToThatPoint()
    {
        var f = new PlannerFixture(CoordinatePlace("Cafe", 32.1052, 34.8051));
        f.Vm.SetDestination(f.Vm.Choices.First(choice => choice.Place?.Name == "Cafe"));

        await f.Vm.PlanCommand.ExecuteAsync(null);

        var request = f.Planner.LastRequest;
        Assert.NotNull(request);
        Assert.Equal(32.09, request.OriginLatitude);
        Assert.Equal(32.1052, request.DestinationLatitude);
        Assert.Equal(34.8051, request.DestinationLongitude);
    }

    [Fact]
    public async Task Planner_CoordinateOnlyOrigin_PlansFromThatPoint()
    {
        var f = new PlannerFixture(
            CoordinatePlace("Cafe", 32.1052, 34.8051),
            new SavedPlace { Name = "Work", StopCode = "200" });
        f.Vm.SelectedOrigin = f.Vm.Choices.First(choice => choice.Place?.Name == "Cafe");
        f.Vm.SetDestination(f.Vm.Choices.First(choice => choice.Place?.Name == "Work"));

        await f.Vm.PlanCommand.ExecuteAsync(null);

        var request = f.Planner.LastRequest;
        Assert.NotNull(request);
        Assert.Equal(32.1052, request.OriginLatitude);
        Assert.Equal(34.8051, request.OriginLongitude);
        Assert.Equal(There.Latitude, request.DestinationLatitude);
    }

    // --- stops tab

    [Fact]
    public async Task Stops_CoordinateOnlyStartPlace_SortsByDistanceFromThePoint()
    {
        var settings = new MemorySettings();
        var places = new SavedPlaceCollection(settings);
        var location = new FakeLocation();
        var stops = new StopsViewModel(
            settings, location, new FakeCatalog(Here, There), new NullAggregator(), places, new TemplateLocalizer(),
            new FixedClock(Now), new ImmediateDispatcher(), NullLogger<StopsViewModel>.Instance);
        places.Items.Add(CoordinatePlace("Cafe", 32.1098, 34.8000));

        stops.SelectedAnchor = places.Items[0];
        await stops.LoadCommand.ExecuteAsync(null);

        Assert.Equal(0, location.Calls);
        Assert.Equal(["200", "100"], stops.Stops.Select(stop => stop.Code).ToArray());
    }

    // --- lines tab

    private sealed class LinesFixture
    {
        public LinesFixture()
        {
            Settings = new MemorySettings();
            Places = new SavedPlaceCollection(Settings);
            Schedule = new LinesViewModelTests.FakeSchedule();
            Vm = new LinesViewModel(
                Settings, Places, new TemplateLocalizer(), new FakeCatalog(Here, There), new FakeLocation(), Schedule,
                new FixedClock(Now), NullLogger<LinesViewModel>.Instance);
        }

        public MemorySettings Settings { get; }
        public SavedPlaceCollection Places { get; }
        public LinesViewModelTests.FakeSchedule Schedule { get; }
        public LinesViewModel Vm { get; }
    }

    [Fact]
    public async Task Lines_CoordinateOnlyOrigin_UsesTheNearestStop()
    {
        var f = new LinesFixture();
        f.Places.Items.Add(CoordinatePlace("Cafe", 32.1097, 34.8000));
        f.Vm.SelectedOrigin = f.Vm.Origins.Single(choice => choice.Place is not null);
        f.Vm.LineName = "4";

        await f.Vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(There.Id, f.Schedule.LastStopId);
    }

    [Fact]
    public async Task Lines_CoordinateOnlyOriginFarFromAnyStop_ReportsNoStopsNearby()
    {
        var f = new LinesFixture();
        f.Places.Items.Add(CoordinatePlace("Remote", 32.5, 34.9));
        f.Vm.SelectedOrigin = f.Vm.Origins.Single(choice => choice.Place is not null);
        f.Vm.LineName = "4";

        await f.Vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("NoStopsNearby", f.Vm.StatusText);
        Assert.Equal(0, f.Schedule.StopDepartureCalls);
    }

    // --- polling service

    private sealed class RecordingAggregator : ILiveDataAggregator
    {
        public List<string> Codes { get; } = [];

        public Task<AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default)
        {
            Codes.Add(stopCode);
            return Task.FromResult(new AggregatedArrivals([], new Dictionary<ArrivalSource, ProviderStatus>()));
        }
    }

    private static (SchedulePollingService Service, RecordingAggregator Aggregator) Poller(SavedPlace start)
    {
        var destination = new SavedPlace { Name = "Dest", StopCode = "200" };
        var config = new AppConfig
        {
            SavedPlaces = [start, destination],
            StartPlaceId = start.Id,
            DestinationPlaceId = destination.Id,
            LineName = "4"
        };
        var aggregator = new RecordingAggregator();
        return (new SchedulePollingService(aggregator, config, new FakeCatalog(Here, There), NullLogger<SchedulePollingService>.Instance), aggregator);
    }

    [Fact]
    public async Task Polling_CoordinateOnlyStart_PollsTheNearestStop()
    {
        var (service, aggregator) = Poller(CoordinatePlace("Cafe", 32.1097, 34.8000));

        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(["200"], aggregator.Codes);
    }

    [Fact]
    public async Task Polling_CoordinateOnlyStartWithNoStopNearby_SkipsWithoutThrowing()
    {
        var (service, aggregator) = Poller(CoordinatePlace("Remote", 32.5, 34.9));

        await service.PollOnceAsync(CancellationToken.None);

        Assert.Empty(aggregator.Codes);
    }

    // --- config compatibility

    [Fact]
    public void OldConfigJson_WithOnlyNameAndStopCode_LoadsUnchanged()
    {
        const string json = """
            {"SavedPlaces":[{"Id":"abc","Name":"Home","StopCode":"4147"}],"StartPlaceId":"abc"}
            """;
        var options = new JsonSerializerOptions { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

        var config = JsonSerializer.Deserialize<AppConfig>(json, options)!;
        config.Validate();

        var place = Assert.Single(config.SavedPlaces);
        Assert.Equal("4147", place.StopCode);
        Assert.Null(place.Latitude);
        Assert.Null(place.Longitude);
        Assert.Null(place.Address);
    }

    [Fact]
    public async Task CoordinateOnlyPlace_RoundTripsThroughTheEncryptedStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "BusPulseTests-" + Guid.NewGuid().ToString("N"), "settings.dpapi");
        try
        {
            var store = new AppConfigStore(path);
            var config = new AppConfig { SavedPlaces = [CoordinatePlace("Cafe", 32.1052, 34.8051), new SavedPlace { Name = "Old", StopCode = "4147" }] };

            await store.SaveAsync(config);
            var loaded = await store.LoadAsync();

            var cafe = loaded.SavedPlaces.Single(place => place.Name == "Cafe");
            Assert.Equal(string.Empty, cafe.StopCode);
            Assert.Equal(32.1052, cafe.Latitude);
            Assert.Equal(34.8051, cafe.Longitude);
            Assert.Equal("1 Main St", cafe.Address);
            Assert.Equal("4147", loaded.SavedPlaces.Single(place => place.Name == "Old").StopCode);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class NullAggregator : ILiveDataAggregator
    {
        public Task<AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AggregatedArrivals([], new Dictionary<ArrivalSource, ProviderStatus>()));
    }
}
