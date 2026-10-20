using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.LiveData;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class ViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T19:26:00+03:00");

    [Fact]
    public async Task Shell_ToggleMute_UpdatesConfigAndPersists()
    {
        var f = new Fixture();

        await f.Shell.ToggleMuteCommand.ExecuteAsync(null);

        Assert.True(f.Shell.IsMuted);
        Assert.True(f.Settings.Config.NotificationsMuted);
        Assert.Equal(1, f.Settings.SaveCount);
        Assert.Equal("UnmuteNotifications", f.Shell.MuteButtonText);
        Assert.Contains("muted", f.Shell.TrayText);
    }

    [Fact]
    public async Task Shell_ToggleLanguage_SwitchesLocalizerAndFlowDirection()
    {
        var f = new Fixture();

        await f.Shell.ToggleLanguageCommand.ExecuteAsync(null);

        Assert.Equal("he", f.Settings.Config.Language);
        Assert.Equal("he", f.Localizer.Language);
        Assert.Equal(System.Windows.FlowDirection.RightToLeft, f.Shell.FlowDirection);
        Assert.Equal("English", f.Shell.LanguageButtonText);
    }

    [Fact]
    public async Task Shell_SetProfile_PersistsProfile()
    {
        var f = new Fixture();

        await f.Shell.SetProfileCommand.ExecuteAsync("Work");

        Assert.Equal("Work", f.Shell.ActiveProfile);
        Assert.Equal("Work", f.Settings.Config.ActiveProfile);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(99, 0)]
    [InlineData(-1, 0)]
    public void Shell_SelectTab_IgnoresOutOfRangeIndexes(int index, int expected)
    {
        var f = new Fixture();

        f.Shell.SelectTabCommand.Execute(index);

        Assert.Equal(expected, f.Shell.SelectedTabIndex);
    }

    [Fact]
    public async Task TripPlanner_WithoutSelection_ShowsSelectRouteMessage()
    {
        var f = new Fixture();

        await f.TripPlanner.CheckRouteCommand.ExecuteAsync(null);

        Assert.Equal("SelectRouteMessage", f.TripPlanner.StatusText);
        Assert.Equal(0, f.Aggregator.Calls);
    }

    [Fact]
    public async Task TripPlanner_ReportsLiveAndScheduledMatchesForTheLine()
    {
        var f = new Fixture();
        f.AddPlaces("Home", "3490", "Work", "1111");
        f.TripPlanner.SelectedStart = f.TripPlanner.Places[0];
        f.TripPlanner.SelectedDestination = f.TripPlanner.Places[1];
        f.TripPlanner.LineName = "4";
        f.Aggregator.Result = new AggregatedArrivals(
        [
            Arrival("4", live: true), Arrival("4", live: false), Arrival("9", live: true)
        ], new Dictionary<ArrivalSource, ProviderStatus>());

        await f.TripPlanner.CheckRouteCommand.ExecuteAsync(null);

        Assert.Equal("MatchesFoundAtStart: 2 (1 LiveBadge, 1 ScheduledBadge)", f.TripPlanner.StatusText);
        Assert.Equal("3490", f.Aggregator.LastStopCode);
        Assert.Equal("4", f.Settings.Config.LineName);
        Assert.Equal("Home → Work", f.TripPlanner.RouteSummary);
    }

    [Fact]
    public async Task TripPlanner_WhenSaveFails_ReportsItAndSkipsTheNetwork()
    {
        var f = new Fixture();
        f.AddPlaces("Home", "3490", "Work", "1111");
        f.TripPlanner.SelectedStart = f.TripPlanner.Places[0];
        f.TripPlanner.SelectedDestination = f.TripPlanner.Places[1];
        f.TripPlanner.LineName = "4";
        f.Settings.SaveSucceeds = false;

        await f.TripPlanner.CheckRouteCommand.ExecuteAsync(null);

        Assert.Equal("PlaceSaveFailed", f.TripPlanner.StatusText);
        Assert.Equal(0, f.Aggregator.Calls);
    }

    [Fact]
    public async Task Settings_AddPlace_SavesAndClearsTheForm()
    {
        var f = new Fixture();
        f.SettingsTab.NewPlaceName = "Home";
        f.SettingsTab.NewPlaceStopCode = "3490";

        await f.SettingsTab.AddPlaceCommand.ExecuteAsync(null);

        var place = Assert.Single(f.SettingsTab.Places);
        Assert.Equal("3490", place.StopCode);
        Assert.Single(f.Settings.Config.SavedPlaces);
        Assert.Equal("PlaceSaved", f.SettingsTab.PlaceStatus);
        Assert.Equal(string.Empty, f.SettingsTab.NewPlaceStopCode);
    }

    [Theory]
    [InlineData("", "3490")]
    [InlineData("Home", "")]
    [InlineData("Home", "34 90;")]
    public async Task Settings_AddPlace_RejectsInvalidInput(string name, string stopCode)
    {
        var f = new Fixture();
        f.SettingsTab.NewPlaceName = name;
        f.SettingsTab.NewPlaceStopCode = stopCode;

        await f.SettingsTab.AddPlaceCommand.ExecuteAsync(null);

        Assert.Empty(f.SettingsTab.Places);
        Assert.Equal("InvalidPlaceMessage", f.SettingsTab.PlaceStatus);
    }

    [Fact]
    public async Task Settings_AddPlace_RejectsDuplicateNames()
    {
        var f = new Fixture();
        f.AddPlaces("Home", "3490");
        f.SettingsTab.NewPlaceName = "home";
        f.SettingsTab.NewPlaceStopCode = "1111";

        await f.SettingsTab.AddPlaceCommand.ExecuteAsync(null);

        Assert.Single(f.SettingsTab.Places);
        Assert.Equal("PlaceAlreadyExists", f.SettingsTab.PlaceStatus);
    }

    [Fact]
    public async Task Settings_CheckConnection_WithoutPlaces_AsksForOne()
    {
        var f = new Fixture();

        await f.SettingsTab.CheckConnectionCommand.ExecuteAsync(null);

        var item = Assert.Single(f.SettingsTab.ProviderStatuses);
        Assert.Equal("AddPlaceFirst", item.Status);
    }

    [Fact]
    public async Task Settings_CheckConnection_ReportsEachProviderAndTheSchedule()
    {
        var f = new Fixture();
        f.AddPlaces("Home", "3490");
        f.Curlbus.Result = new ProviderResult(ProviderStatus.Ok, [Arrival("4", live: true)]);
        f.Rail.Result = ProviderResult.Failed(ProviderStatus.Disabled, "no key");
        f.Schedule.Result = new ProviderResult(ProviderStatus.NoData, []);

        await f.SettingsTab.CheckConnectionCommand.ExecuteAsync(null);

        Assert.Equal(
            ["ProviderCurlbus: StatusOk (1)", "ProviderRail: StatusDisabled", "ProviderSchedule: StatusNoData"],
            f.SettingsTab.ProviderStatuses.Select(item => $"{item.Name}: {item.Status}").ToArray());
    }

    private static Arrival Arrival(string line, bool live) =>
        new("3490", line, "Dan", "X", Now.AddMinutes(5), live, live ? ArrivalSource.Curlbus : ArrivalSource.Schedule, Now);

    private sealed class Fixture
    {
        public Fixture()
        {
            Settings = new FakeSettings();
            Localizer = new FakeLocalizer();
            var places = new SavedPlaceCollection(Settings);
            Places = places;
            Aggregator = new FakeAggregator();
            Curlbus = new FakeProvider(ArrivalSource.Curlbus);
            Rail = new FakeProvider(ArrivalSource.Rail);
            Schedule = new FakeSchedule();
            TripPlanner = new TripPlannerViewModel(Settings, places, Localizer, Aggregator);
            SettingsTab = new SettingsViewModel(Settings, places, Localizer, [Rail, Curlbus], Schedule);
            Shell = new ShellViewModel(
                Settings,
                Localizer,
                new StopsViewModel(
                    new FakeLocation(), new FakeCatalog(), Aggregator, places, Localizer, new FixedClock(Now),
                    new ImmediateDispatcher(), NullLogger<StopsViewModel>.Instance),
                TripPlanner,
                new LinesViewModel(Localizer),
                SettingsTab);
        }

        public FakeSettings Settings { get; }
        public FakeLocalizer Localizer { get; }
        public SavedPlaceCollection Places { get; }
        public FakeAggregator Aggregator { get; }
        public FakeProvider Curlbus { get; }
        public FakeProvider Rail { get; }
        public FakeSchedule Schedule { get; }
        public TripPlannerViewModel TripPlanner { get; }
        public SettingsViewModel SettingsTab { get; }
        public ShellViewModel Shell { get; }

        public void AddPlaces(params string[] nameAndCode)
        {
            for (var i = 0; i < nameAndCode.Length; i += 2)
            {
                Places.Items.Add(new SavedPlace { Name = nameAndCode[i], StopCode = nameAndCode[i + 1] });
            }

            Places.SyncToConfig();
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppConfig Config { get; } = new();
        public int SaveCount { get; private set; }
        public bool SaveSucceeds { get; set; } = true;

        public Task<bool> SaveAsync()
        {
            SaveCount++;
            return Task.FromResult(SaveSucceeds);
        }
    }

    private sealed class FakeLocalizer : ILocalizer
    {
        public string Language { get; private set; } = "en";

        public string Get(string key) => key;

        public void SetLanguage(string language) => Language = language;
    }

    private sealed class FakeAggregator : ILiveDataAggregator
    {
        public int Calls { get; private set; }
        public string? LastStopCode { get; private set; }
        public AggregatedArrivals Result { get; set; } = new([], new Dictionary<ArrivalSource, ProviderStatus>());

        public Task<AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastStopCode = stopCode;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeProvider(ArrivalSource source) : ILiveArrivalsProvider
    {
        public ArrivalSource Source { get; } = source;
        public ProviderResult Result { get; set; } = new(ProviderStatus.NoData, []);

        public Task<ProviderResult> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    private sealed class FakeSchedule : IScheduleProvider
    {
        public ProviderResult Result { get; set; } = new(ProviderStatus.NoData, []);

        public Task<ProviderResult> GetScheduleAsync(string stopCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }
}
