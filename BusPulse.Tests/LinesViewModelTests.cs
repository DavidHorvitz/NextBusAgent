using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class LinesViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop Origin = new("100", "Origin stop", "City", 32.1, 34.8, Id: 1);

    [Fact]
    public async Task Load_ListsEachDirectionOfTheLineFromTheOriginStop_EarliestFirst()
    {
        var f = new Fixture();
        f.Schedule.Departures =
        [
            Departure(ride: 10, minutes: 20, line: "4", routeName: "Origin<->Dest B-City-1"),
            Departure(ride: 11, minutes: 5, line: "4", routeName: "Origin<->Dest A-City-1"),
            Departure(ride: 12, minutes: 8, line: "9", routeName: "Origin<->Dest C-City-1")
        ];
        f.Vm.LineName = "4";

        await f.Vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(["Dest A", "Dest B"], f.Vm.Directions.Select(d => d.Destination).ToArray());
        Assert.Equal(11, f.Vm.SelectedDirection!.RideId);
        Assert.Equal(string.Empty, f.Vm.StatusText);
    }

    [Fact]
    public async Task Load_LineNotServedFromThisStop_SaysSo()
    {
        var f = new Fixture();
        f.Schedule.Departures = [Departure(ride: 10, minutes: 5, line: "9", routeName: "Origin<->Dest-City-1")];
        f.Vm.LineName = "4";

        await f.Vm.LoadCommand.ExecuteAsync(null);

        Assert.Empty(f.Vm.Directions);
        Assert.Equal("LinesNoService", f.Vm.StatusText);
    }

    [Fact]
    public async Task Load_BlankOrOverlongLineName_AsksForOne()
    {
        var f = new Fixture();

        f.Vm.LineName = "   ";
        await f.Vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("LinesEnterLine", f.Vm.StatusText);

        f.Vm.LineName = new string('1', 33);
        await f.Vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("LinesEnterLine", f.Vm.StatusText);
        Assert.Equal(0, f.Schedule.StopDepartureCalls);
    }

    [Fact]
    public async Task SelectingADirection_ListsStopsInCallingOrder_AndMarksTheOriginStop()
    {
        var f = new Fixture();
        f.Schedule.Departures = [Departure(ride: 10, minutes: 5, line: "4", routeName: "Origin<->Dest-City-1")];
        f.Schedule.RideStops =
        [
            RideRow(ride: 10, sequence: 3, code: "300", name: "Third", minutes: 15),
            RideRow(ride: 10, sequence: 1, code: "100", name: "Origin stop", minutes: 5),
            RideRow(ride: 10, sequence: 2, code: "200", name: "Second", minutes: 10)
        ];
        f.Vm.LineName = "4";

        await f.Vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(["Origin stop", "Second", "Third"], f.Vm.Stops.Select(stop => stop.Name).ToArray());
        Assert.True(f.Vm.Stops[0].IsOrigin);
        Assert.False(f.Vm.Stops[1].IsOrigin);
        Assert.Equal("Origin stop", f.Vm.Stops[0].Name);
    }

    [Fact]
    public async Task PinningALine_PersistsItAndTheButtonTextFollows()
    {
        var f = new Fixture();
        f.Vm.LineName = "4";
        Assert.Equal("LinePin", f.Vm.PinText);

        f.Vm.TogglePinLineCommand.Execute(null);

        Assert.Equal(["4"], f.Settings.Config.FavoriteLines);
        Assert.True(f.Vm.IsFavoriteLine);
        Assert.Equal("LineUnpin", f.Vm.PinText);

        f.Vm.TogglePinLineCommand.Execute(null);

        Assert.Empty(f.Settings.Config.FavoriteLines);
        Assert.Equal("LinePin", f.Vm.PinText);
    }

    [Fact]
    public void SelectingAFavorite_FillsTheLineName()
    {
        var f = new Fixture();

        f.Vm.SelectFavoriteLineCommand.Execute("9");

        Assert.Equal("9", f.Vm.LineName);
    }

    [Fact]
    public async Task OriginPlaceMissingFromTheCatalog_ReportsIt()
    {
        var f = new Fixture();
        f.Places.Items.Add(new SavedPlace { Name = "Gone", StopCode = "99999" });
        f.Vm.SelectedOrigin = f.Vm.Origins.Single(choice => choice.Place is not null);
        f.Vm.LineName = "4";

        await f.Vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("PlaceNotFound", f.Vm.StatusText);
    }

    [Fact]
    public async Task MyLocationDenied_ExplainsInsteadOfLoading()
    {
        var f = new Fixture();
        f.Vm.SelectedOrigin = f.Vm.MyLocation;
        f.Location.Result = LocationResult.Failed(LocationStatus.PermissionDenied);
        f.Vm.LineName = "4";

        await f.Vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("LocationPermissionDenied", f.Vm.StatusText);
        Assert.Equal(0, f.Schedule.StopDepartureCalls);
    }

    [Fact]
    public async Task MyLocation_UsesTheNearestStop()
    {
        var f = new Fixture();
        f.Vm.SelectedOrigin = f.Vm.MyLocation;
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.1, 34.8, 20);
        f.Schedule.Departures = [Departure(ride: 10, minutes: 5, line: "4", routeName: "Origin<->Dest-City-1")];
        f.Vm.LineName = "4";

        await f.Vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(Origin.Id, f.Schedule.LastStopId);
        Assert.Equal(1, f.Location.Calls);
    }

    private static RideStop Departure(long ride, int minutes, string line, string routeName) =>
        new(ride, Origin.Id, 1, Now.AddMinutes(minutes), line, "Dan", routeName, "Origin stop", Origin.Code);

    private static RideStop RideRow(long ride, int sequence, string code, string name, int minutes) =>
        new(ride, 0, sequence, Now.AddMinutes(minutes), "4", "Dan", null, name, code);

    private sealed class Fixture
    {
        public Fixture()
        {
            Settings = new MemorySettings();
            Places = new SavedPlaceCollection(Settings);
            Location = new FakeLocation();
            Schedule = new FakeSchedule();
            Vm = new LinesViewModel(
                Settings, Places, new TemplateLocalizer(), new FakeCatalog(Origin), Location, Schedule,
                new FixedClock(Now), NullLogger<LinesViewModel>.Instance);
            Vm.SelectedOrigin = new LocationChoice("Origin stop", stop: Origin);
        }

        public MemorySettings Settings { get; }
        public SavedPlaceCollection Places { get; }
        public FakeLocation Location { get; }
        public FakeSchedule Schedule { get; }
        public LinesViewModel Vm { get; }
    }

    internal sealed class FakeSchedule : IRideScheduleSource
    {
        public List<RideStop> Departures { get; set; } = [];
        public List<RideStop> RideStops { get; set; } = [];
        public int StopDepartureCalls { get; private set; }
        public long? LastStopId { get; private set; }

        public Task<IReadOnlyList<RideStop>> GetStopDeparturesAsync(long stopId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
        {
            StopDepartureCalls++;
            LastStopId = stopId;
            return Task.FromResult<IReadOnlyList<RideStop>>(Departures);
        }

        public Task<IReadOnlyList<RideStop>> GetRideStopsAsync(long rideId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RideStop>>(RideStops.Where(row => row.RideId == rideId).ToArray());

        public Task<IReadOnlyList<RideStop>> GetRideStopsAtAsync(IReadOnlyCollection<long> rideIds, IReadOnlyCollection<long> stopIds, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RideStop>>([]);
    }
}
