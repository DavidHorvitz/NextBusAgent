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

public sealed class ShellAndSettingsTests
{
    [Fact]
    public async Task Shell_ToggleMute_UpdatesConfigPersistsAndMirrorsToSettings()
    {
        var f = new Fixture();

        await f.Shell.ToggleMuteCommand.ExecuteAsync(null);

        Assert.True(f.Shell.IsMuted);
        Assert.True(f.Settings.Config.NotificationsMuted);
        Assert.Equal(1, f.Settings.SaveCount);
        Assert.Equal("UnmuteNotifications", f.Shell.MuteButtonText);
        Assert.Equal("TrayMuted", f.Shell.TrayText);
        Assert.False(f.SettingsTab.NotificationsEnabled);
    }

    [Fact]
    public void Settings_NotificationsSwitch_MirrorsToTheShellAndConfig()
    {
        var f = new Fixture();

        f.SettingsTab.NotificationsEnabled = false;

        Assert.True(f.Shell.IsMuted);
        Assert.True(f.Settings.Config.NotificationsMuted);
        Assert.Equal("TrayResume", f.Shell.TrayMenuMuteText);

        f.SettingsTab.NotificationsEnabled = true;

        Assert.False(f.Shell.IsMuted);
        Assert.False(f.Settings.Config.NotificationsMuted);
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
    public void Settings_LanguageDropdown_SwitchesTheWholeUi_AndTheHeaderButtonStaysInSync()
    {
        var f = new Fixture();

        f.SettingsTab.SelectedLanguage = f.SettingsTab.Languages.Single(option => option.Code == "he");

        Assert.Equal("he", f.Settings.Config.Language);
        Assert.Equal("he", f.Localizer.Language);
        Assert.Equal(System.Windows.FlowDirection.RightToLeft, f.Shell.FlowDirection);
    }

    [Fact]
    public async Task Shell_LanguageButton_UpdatesTheSettingsDropdown()
    {
        var f = new Fixture();

        await f.Shell.ToggleLanguageCommand.ExecuteAsync(null);

        Assert.Equal("he", f.SettingsTab.SelectedLanguage.Code);
    }

    [Fact]
    public void Settings_TestNotification_SendsOne_OrExplainsThatNotificationsAreOff()
    {
        var f = new Fixture();

        f.SettingsTab.SendTestNotificationCommand.Execute(null);

        Assert.Equal("TestNotificationSent", f.SettingsTab.TestStatus);
        Assert.Equal(("TestNotificationTitle", "TestNotificationBody", NotificationKind.Info), Assert.Single(f.Notifier.Sent));

        f.SettingsTab.NotificationsEnabled = false;
        f.SettingsTab.SendTestNotificationCommand.Execute(null);

        Assert.Equal("NotificationsOffStatus", f.SettingsTab.TestStatus);
        Assert.Single(f.Notifier.Sent);
    }

    [Fact]
    public void Settings_DefaultsMatchTheConfig_AndOffListValuesSnapToTheNearestOption()
    {
        var f = new Fixture(config => { config.LeadMinutes = 7; config.PollingSeconds = 60; });

        Assert.Equal(5, f.SettingsTab.SelectedLeadMinutes);
        Assert.Equal(30, f.SettingsTab.SelectedPollingSeconds);
        Assert.True(f.SettingsTab.NotificationsEnabled);
        Assert.Equal([0, 2, 5, 10, 15], f.SettingsTab.LeadOptions);
        Assert.Equal([15, 20, 30], f.SettingsTab.PollingOptions);
    }

    [Fact]
    public void Settings_ShippedDefaults_AreFiveMinutesAndTwentySeconds()
    {
        var config = new AppConfig();

        Assert.Equal(5, config.LeadMinutes);
        Assert.Equal(20, config.PollingSeconds);
        config.Validate();
    }

    [Fact]
    public void Settings_LeadAndPolling_ArePersistedToTheConfig()
    {
        var f = new Fixture();

        f.SettingsTab.SelectedLeadMinutes = 10;
        f.SettingsTab.SelectedPollingSeconds = 15;

        Assert.Equal(10, f.Settings.Config.LeadMinutes);
        Assert.Equal(15, f.Settings.Config.PollingSeconds);
        Assert.Equal(2, f.Settings.SaveCount);
    }

    [Fact]
    public void Settings_HomeAndWork_CanBeSetAndCleared()
    {
        var f = new Fixture();
        f.AddPlaces("Home", "3490", "Office", "1111");

        f.SettingsTab.HomePlace = f.Places.Items[0];
        f.SettingsTab.WorkPlace = f.Places.Items[1];

        Assert.Equal(f.Places.Items[0].Id, f.Settings.Config.HomePlaceId);
        Assert.Equal(f.Places.Items[1].Id, f.Settings.Config.WorkPlaceId);

        f.SettingsTab.ClearHomeCommand.Execute(null);

        Assert.Null(f.Settings.Config.HomePlaceId);
        f.Settings.Config.Validate();
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("3", 3)]
    [InlineData("4", 0)]
    [InlineData("-1", 0)]
    [InlineData("abc", 0)]
    [InlineData("", 0)]
    public void Shell_SelectTab_AcceptsTextParametersFromKeyBindings_AndIgnoresBadOnes(string parameter, int expected)
    {
        // XAML passes CommandParameter="1" as a string; a strongly typed command threw and crashed the app.
        var f = new Fixture();

        Assert.True(f.Shell.SelectTabCommand.CanExecute(parameter));
        f.Shell.SelectTabCommand.Execute(parameter);

        Assert.Equal(expected, f.Shell.SelectedTabIndex);
    }

    [Fact]
    public void Shell_SelectTab_IgnoresNull()
    {
        var f = new Fixture();

        f.Shell.SelectTabCommand.Execute(null);

        Assert.Equal(0, f.Shell.SelectedTabIndex);
    }

    [Fact]
    public void CtrlF_OpensThePlannerAndRequestsSearchFocus()
    {
        var f = new Fixture();
        var requested = 0;
        f.Shell.TripPlanner.SearchFocusRequested += (_, _) => requested++;

        f.Shell.FocusSearchCommand.Execute(null);

        Assert.Equal(1, f.Shell.SelectedTabIndex);
        Assert.Equal(1, requested);
    }

    [Fact]
    public void Settings_Theme_DefaultsToSystem_AndAnUnknownStoredValueFallsBack()
    {
        Assert.Equal("system", new Fixture().SettingsTab.SelectedTheme.Code);
        Assert.Equal(["system", "light", "dark"], new Fixture().SettingsTab.Themes.Select(t => t.Code).ToArray());
        Assert.Equal("dark", new Fixture(c => c.Theme = "dark").SettingsTab.SelectedTheme.Code);
    }

    [Fact]
    public void Settings_PickingATheme_PersistsItAndAppliesItThroughTheThemeService()
    {
        var f = new Fixture();

        f.SettingsTab.SelectedTheme = f.SettingsTab.Themes.Single(t => t.Code == "dark");

        Assert.Equal("dark", f.Settings.Config.Theme);
        Assert.Equal(["dark"], f.Theme.Applied);
        Assert.Equal(1, f.Settings.SaveCount);
        f.Settings.Config.Validate();
    }

    [Fact]
    public async Task Settings_ClearFavorites_EmptiesBothListsAndRefreshesTheTabs()
    {
        var f = new Fixture();
        f.Settings.Config.FavoriteStopCodes = ["3490"];
        f.Settings.Config.FavoriteLines = ["4", "9"];
        f.Shell.Lines.SelectFavoriteLineCommand.Execute("4");
        f.Shell.Lines.TogglePinLineCommand.Execute(null);
        f.Shell.Lines.TogglePinLineCommand.Execute(null);

        await f.SettingsTab.ClearFavoritesCommand.ExecuteAsync(null);

        Assert.Empty(f.Settings.Config.FavoriteStopCodes);
        Assert.Empty(f.Settings.Config.FavoriteLines);
        Assert.Empty(f.Shell.Lines.FavoriteLines);
        Assert.False(f.Shell.Lines.IsFavoriteLine);
        Assert.Equal("FavoritesCleared", f.SettingsTab.FavoritesStatus);
    }

    [Fact]
    public async Task Settings_ClearFavorites_WhenSaveFails_SaysSo()
    {
        var f = new Fixture();
        f.Settings.SaveSucceeds = false;

        await f.SettingsTab.ClearFavoritesCommand.ExecuteAsync(null);

        Assert.Equal("SettingsSaveFailed", f.SettingsTab.FavoritesStatus);
    }

    [Fact]
    public void ContextMenu_PlanRouteFromAStop_OpensThePlannerWithThatOrigin()
    {
        var f = new Fixture();
        var stop = new BusPulse.Services.Stops.Stop("3490", "Origin stop", "City", 32.1, 34.8, Id: 1);
        var item = new StopItemViewModel(new BusPulse.Services.Stops.NearbyStop(stop, 50), f.Localizer);

        f.Shell.Stops.PlanRouteFromStopCommand.Execute(item);

        Assert.Equal(1, f.Shell.SelectedTabIndex);
        Assert.Equal("Origin stop", f.Shell.TripPlanner.SelectedOrigin.DisplayName);
        Assert.Same(stop, f.Shell.TripPlanner.SelectedOrigin.Stop);
    }

    [Fact]
    public void ContextMenu_ShowLinesFromAStop_OpensTheLinesTabWithThatOrigin()
    {
        var f = new Fixture();
        var stop = new BusPulse.Services.Stops.Stop("3490", "Origin stop", "City", 32.1, 34.8, Id: 1);
        var item = new StopItemViewModel(new BusPulse.Services.Stops.NearbyStop(stop, 50), f.Localizer);

        f.Shell.Stops.ShowLinesFromStopCommand.Execute(item);

        Assert.Equal(2, f.Shell.SelectedTabIndex);
        Assert.Same(stop, f.Shell.Lines.SelectedOrigin.Stop);
    }

    [Fact]
    public void ContextMenu_NullItem_DoesNothing()
    {
        var f = new Fixture();

        f.Shell.Stops.PlanRouteFromStopCommand.Execute(null);
        f.Shell.Stops.ShowLinesFromStopCommand.Execute(null);

        Assert.Equal(0, f.Shell.SelectedTabIndex);
    }

    [Fact]
    public void Settings_ClearHistory_ClearsTheStoreAndConfirms()
    {
        var f = new Fixture();
        f.History.Record("a", "Home");

        f.SettingsTab.ClearHistoryCommand.Execute(null);

        Assert.Equal(1, f.History.ClearCalls);
        Assert.Equal("HistoryCleared", f.SettingsTab.HistoryStatus);
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

        Assert.Equal("AddPlaceFirst", Assert.Single(f.SettingsTab.ProviderStatuses).Status);
    }

    [Fact]
    public async Task Settings_CheckConnection_ReportsEachProviderAndTheSchedule()
    {
        var f = new Fixture();
        f.AddPlaces("Home", "3490");
        f.Curlbus.Result = new ProviderResult(ProviderStatus.Ok, [new Arrival("3490", "4", null, null, DateTimeOffset.UtcNow, true, ArrivalSource.Curlbus, DateTimeOffset.UtcNow)]);
        f.Rail.Result = ProviderResult.Failed(ProviderStatus.Disabled, "no key");
        f.Schedule.Result = new ProviderResult(ProviderStatus.NoData, []);

        await f.SettingsTab.CheckConnectionCommand.ExecuteAsync(null);

        Assert.Equal(
            ["ProviderCurlbus: StatusOk (1)", "ProviderRail: StatusDisabled", "ProviderSchedule: StatusNoData"],
            f.SettingsTab.ProviderStatuses.Select(item => $"{item.Name}: {item.Status}").ToArray());
    }

    [Fact]
    public async Task Settings_WhenASaveFails_IsReportedInTheSettingsTab()
    {
        var f = new Fixture();
        f.Settings.SaveSucceeds = false;

        f.SettingsTab.SelectedLeadMinutes = 10;
        await Task.Delay(20);

        Assert.Equal("SettingsSaveFailed", f.SettingsTab.SaveStatus);
    }

    private sealed class Fixture
    {
        public Fixture(Action<AppConfig>? configure = null)
        {
            Settings = new MemorySettings();
            configure?.Invoke(Settings.Config);
            Localizer = new TemplateLocalizer();
            Places = new SavedPlaceCollection(Settings);
            History = new FakeHistory();
            Curlbus = new FakeProvider(ArrivalSource.Curlbus);
            Rail = new FakeProvider(ArrivalSource.Rail);
            Schedule = new FakeSchedule();
            Notifier = new RecordingNotifier();
            Theme = new FakeTheme();
            SettingsTab = new SettingsViewModel(Settings, Places, Localizer, [Rail, Curlbus], Schedule, History, Notifier);
            Shell = new ShellViewModel(
                Settings,
                Localizer,
                new StopsViewModel(
                    Settings, new FakeLocation(), new FakeCatalog(), new NullAggregator(), Places, Localizer,
                    new FixedClock(DateTimeOffset.UtcNow), new ImmediateDispatcher(), NullLogger<StopsViewModel>.Instance),
                new TripPlannerViewModel(
                    Settings, Places, Localizer, new FakeTripPlanner(), new FakeCatalog(), new FakeLocation(), History,
                    new FakeReminder(), Notifier, new FixedClock(DateTimeOffset.UtcNow), new ImmediateDispatcher(),
                    NullLogger<TripPlannerViewModel>.Instance),
                new LinesViewModel(Settings, Places, Localizer, new FakeCatalog(), new FakeLocation(), new LinesViewModelTests.FakeSchedule(), new FixedClock(DateTimeOffset.UtcNow), NullLogger<LinesViewModel>.Instance),
                SettingsTab,
                Theme);
        }

        public MemorySettings Settings { get; }
        public TemplateLocalizer Localizer { get; }
        public SavedPlaceCollection Places { get; }
        public FakeHistory History { get; }
        public RecordingNotifier Notifier { get; }
        public FakeTheme Theme { get; }
        public FakeProvider Curlbus { get; }
        public FakeProvider Rail { get; }
        public FakeSchedule Schedule { get; }
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

    private sealed class NullAggregator : ILiveDataAggregator
    {
        public Task<AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AggregatedArrivals([], new Dictionary<ArrivalSource, ProviderStatus>()));
    }

    internal sealed class FakeProvider(ArrivalSource source) : ILiveArrivalsProvider
    {
        public ArrivalSource Source { get; } = source;
        public ProviderResult Result { get; set; } = new(ProviderStatus.NoData, []);

        public Task<ProviderResult> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }

    internal sealed class FakeSchedule : IScheduleProvider
    {
        public ProviderResult Result { get; set; } = new(ProviderStatus.NoData, []);

        public Task<ProviderResult> GetScheduleAsync(string stopCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result);
    }
}

public sealed class TripPlannerViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop HomeStop = new("100", "Home stop", "City", 32.1000, 34.8000, Id: 1);
    private static readonly Stop WorkStop = new("200", "Work stop", "City", 32.1100, 34.8000, Id: 2);

    [Fact]
    public void AlwaysStartsFromTheCurrentLocation_EvenWithASavedStartPlace()
    {
        var f = new Fixture(config => config.StartPlaceId = "whatever");

        Assert.True(f.Vm.SelectedOrigin.IsMyLocation);
        Assert.Equal("MyLocation", f.Vm.SelectedOrigin.DisplayName);
    }

    [Fact]
    public async Task Plan_WithoutDestination_AsksForOne()
    {
        var f = new Fixture();

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal("SetDestinationFirst", f.Vm.StatusText);
        Assert.Equal(0, f.Planner.Calls);
    }

    [Fact]
    public async Task Plan_UsesRealLocationForTheOriginAndTheStopForTheDestination()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.0900, 34.7900, 20);
        f.Vm.SetDestination(f.Choice("Work"));
        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [Option(10, 5)]);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal(1, f.Location.Calls);
        Assert.Equal(32.0900, f.Planner.LastRequest!.OriginLatitude);
        Assert.Equal(WorkStop.Latitude, f.Planner.LastRequest.DestinationLatitude);
        Assert.Equal(TripTimeMode.Now, f.Planner.LastRequest.Mode);
        Assert.Single(f.Vm.Options);
        Assert.Equal(string.Empty, f.Vm.StatusText);
        Assert.Equal(f.Places.Items[1].Id, f.Settings.Config.DestinationPlaceId);
        Assert.Equal("Work", f.History.Entries[0].Name);
    }

    [Fact]
    public async Task Plan_WhenLocationIsDenied_ExplainsAndDoesNotPlan()
    {
        var f = new Fixture();
        f.Location.Result = LocationResult.Failed(LocationStatus.PermissionDenied);
        f.Vm.SetDestination(f.Choice("Work"));

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal("LocationPermissionDenied", f.Vm.StatusText);
        Assert.Equal(0, f.Planner.Calls);
    }

    [Fact]
    public async Task Plan_WhenADestinationStopIsMissing_ReportsIt()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.09, 34.79, 20);
        f.Places.Items.Add(new SavedPlace { Name = "Gone", StopCode = "99999" });
        f.Vm.SetDestination(f.Choice("Gone"));

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal("PlaceNotFound", f.Vm.StatusText);
        Assert.Equal(0, f.Planner.Calls);
    }

    [Fact]
    public async Task Plan_ReportsEachPlannerStatus()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.09, 34.79, 20);
        f.Vm.SetDestination(f.Choice("Work"));

        f.Planner.Plan = new TripPlan(TripPlanStatus.NoStopsNearOrigin, []);
        await f.Vm.PlanCommand.ExecuteAsync(null);
        Assert.Equal("PlanNoStopsNearOrigin", f.Vm.StatusText);

        f.Planner.Plan = new TripPlan(TripPlanStatus.NoStopsNearDestination, []);
        await f.Vm.PlanCommand.ExecuteAsync(null);
        Assert.Equal("PlanNoStopsNearDestination", f.Vm.StatusText);

        f.Planner.Plan = new TripPlan(TripPlanStatus.NoOptions, []);
        await f.Vm.PlanCommand.ExecuteAsync(null);
        Assert.Equal("PlanNoOptions", f.Vm.StatusText);
    }

    [Fact]
    public async Task Plan_ArriveBy_SendsTheModeAndATargetTime()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.09, 34.79, 20);
        f.Vm.SetDestination(f.Choice("Work"));
        f.Vm.SelectedModeIndex = 2;
        f.Vm.TargetTime = TimeSpan.FromHours(23) + TimeSpan.FromMinutes(30);
        f.Settings.Config.BufferMinutes = 3;

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal(TripTimeMode.ArriveBy, f.Planner.LastRequest!.Mode);
        Assert.NotNull(f.Planner.LastRequest.TargetTime);
        Assert.Equal(3, f.Planner.LastRequest.BufferMinutes);
        Assert.True(f.Vm.IsTimeSelectable);
    }

    [Fact]
    public async Task Plan_RejectsSameOriginAndDestination()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Vm.MyLocation);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal("SameOriginDestination", f.Vm.StatusText);
    }

    [Fact]
    public void ChoosingATime_WithoutASelectedRoute_PromptsToSelectOne_AtMostOncePerMinute()
    {
        var f = new Fixture();

        f.Vm.SelectedModeIndex = 1;
        f.Vm.TargetTime = TimeSpan.FromHours(9);

        Assert.Equal(("BusPulse", "PromptSelectRoute", NotificationKind.Info), Assert.Single(f.Notifier.Sent));

        f.Clock.UtcNow = Now.AddMinutes(2);
        f.Vm.TargetTime = TimeSpan.FromHours(10);

        Assert.Equal(2, f.Notifier.Sent.Count);
    }

    [Fact]
    public void ChoosingATime_WhileARouteIsSelected_DoesNotPrompt()
    {
        var f = new Fixture();
        f.Reminder.Watch(new TripRequest(0, 0, 0, 0), Option(1, 5));

        f.Vm.SelectedModeIndex = 2;

        Assert.Empty(f.Notifier.Sent);
    }

    [Fact]
    public async Task PlanningForALaterTime_WithResults_PromptsToEnableAlerts()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.09, 34.79, 20);
        f.Vm.SetDestination(f.Choice("Work"));
        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [Option(10, 5)]);
        f.Vm.SelectedModeIndex = 1;
        f.Clock.UtcNow = Now.AddMinutes(5);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal("PromptEnableAlerts", f.Notifier.Sent[^1].Message);
    }

    [Fact]
    public void Swap_ExchangesOriginAndDestination()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Choice("Work"));

        f.Vm.SwapCommand.Execute(null);

        Assert.Equal("Work", f.Vm.SelectedOrigin.DisplayName);
        Assert.True(f.Vm.SelectedDestination!.IsMyLocation);
    }

    [Fact]
    public void HomeAndWorkButtons_SetTheDestination()
    {
        var f = new Fixture();
        f.Settings.Config.HomePlaceId = f.Places.Items[0].Id;
        f.Settings.Config.WorkPlaceId = f.Places.Items[1].Id;

        f.Vm.GoWorkCommand.Execute(null);
        Assert.Equal("Work", f.Vm.SelectedDestination!.DisplayName);

        f.Vm.GoHomeCommand.Execute(null);
        Assert.Equal("Home", f.Vm.SelectedDestination!.DisplayName);
        Assert.True(f.Vm.HasHome && f.Vm.HasWork);
    }

    [Fact]
    public async Task Suggestions_FilterAsYouType_RecentFirst()
    {
        var f = new Fixture();
        f.Places.Items.Add(new SavedPlace { Name = "Work annex", StopCode = "300" });
        f.History.Record(f.Places.Items[2].Id, "Work annex");

        f.Vm.DestinationQuery = "wor";
        await f.Vm.UpdateSuggestionsAsync();

        Assert.Equal(["Work annex", "Work"], f.Vm.Suggestions.Select(choice => choice.DisplayName).ToArray());

        f.Vm.ChooseSuggestionCommand.Execute(f.Vm.Suggestions[0]);

        Assert.Equal("Work annex", f.Vm.SelectedDestination!.DisplayName);
        Assert.Empty(f.Vm.Suggestions);
        Assert.Equal(string.Empty, f.Vm.DestinationQuery);
    }

    [Fact]
    public async Task Refresh_UpdatesRowsInPlaceAndTrimsTheTail()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.09, 34.79, 20);
        f.Vm.SetDestination(f.Choice("Work"));
        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [Option(10, 5), Option(11, 9)]);
        await f.Vm.PlanCommand.ExecuteAsync(null);
        var first = f.Vm.Options[0];

        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [Option(12, 4)]);
        await f.Vm.RefreshAsync();

        Assert.Single(f.Vm.Options);
        Assert.Same(first, f.Vm.Options[0]);
        Assert.Equal(12, f.Vm.Options[0].Option.RideId);
    }

    [Fact]
    public async Task Tick_CountsDownTheLeaveTimeFromTheClock()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.09, 34.79, 20);
        f.Vm.SetDestination(f.Choice("Work"));
        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [Option(10, leaveInMinutes: 5)]);
        await f.Vm.PlanCommand.ExecuteAsync(null);
        Assert.Equal("leave in 5m", f.Vm.Options[0].LeaveText);

        f.Clock.UtcNow = Now.AddMinutes(3);
        f.Vm.Tick();

        Assert.Equal("leave in 2m", f.Vm.Options[0].LeaveText);

        f.Clock.UtcNow = Now.AddMinutes(6);
        f.Vm.Tick();

        Assert.Equal("leave now", f.Vm.Options[0].LeaveText);
    }

    [Fact]
    public async Task ToggleReminder_WatchesThenCancelsTheOption_AndMarksTheRow()
    {
        var f = new Fixture();
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.09, 34.79, 20);
        f.Vm.SetDestination(f.Choice("Work"));
        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [Option(10, 5)]);
        await f.Vm.PlanCommand.ExecuteAsync(null);

        f.Vm.ToggleReminderCommand.Execute(f.Vm.Options[0]);

        Assert.Equal(10, f.Reminder.Watched!.RideId);
        Assert.True(f.Vm.Options[0].IsWatched);

        f.Vm.ToggleReminderCommand.Execute(f.Vm.Options[0]);

        Assert.Null(f.Reminder.Watched);
        Assert.False(f.Vm.Options[0].IsWatched);
    }

    [Fact]
    public async Task RouteText_ReadsInTheLayoutDirection()
    {
        var f = new Fixture(config => config.Language = "he");
        f.Location.Result = new LocationResult(LocationStatus.Ok, 32.09, 34.79, 20);
        f.Vm.SetDestination(f.Choice("Work"));
        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [Option(10, 5) with { FromEndpoint = "Bnei Brak", ToEndpoint = "Tel Aviv" }]);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal("Bnei Brak ◄ Tel Aviv", f.Vm.Options[0].RouteText);
        Assert.InRange(f.Vm.Options[0].BearingDegrees, 0, 360);
    }

    [Fact]
    public void Bearing_PointsInTheDirectionOfTravel()
    {
        Assert.Equal(0, Geo.BearingDegrees(32.0, 34.8, 32.1, 34.8), 1);
        Assert.Equal(90, Geo.BearingDegrees(32.0, 34.8, 32.0, 34.9), 0);
        Assert.Equal(180, Geo.BearingDegrees(32.1, 34.8, 32.0, 34.8), 1);
        Assert.Equal(270, Geo.BearingDegrees(32.0, 34.9, 32.0, 34.8), 0);
    }

    [Fact]
    public void WalkText_UnderThirtySeconds_SaysLessThanAMinute()
    {
        var f = new Fixture();
        var brief = Option(10, 5) with { WalkToStop = TimeSpan.FromSeconds(10) };
        var five = Option(10, 5) with { WalkToStop = TimeSpan.FromMinutes(5) };

        Assert.Contains("Less than 1 min walk", new TripOptionViewModel(brief, 0, false, new TemplateLocalizer(), Now).WalkText);
        Assert.Contains("5 min walk", new TripOptionViewModel(five, 0, false, new TemplateLocalizer(), Now).WalkText);
    }

    private static TripOption Option(long rideId, int leaveInMinutes) => new(
        RideId: rideId,
        Line: "4",
        Operator: "Dan",
        FromEndpoint: null,
        ToEndpoint: null,
        OriginStop: HomeStop,
        WalkToStopMeters: 100,
        WalkToStop: TimeSpan.FromMinutes(1),
        DestinationStop: WorkStop,
        WalkFromStopMeters: 100,
        WalkFromStop: TimeSpan.FromMinutes(1),
        BusDeparture: Now.AddMinutes(leaveInMinutes + 2),
        BusArrival: Now.AddMinutes(leaveInMinutes + 20),
        LeaveBy: Now.AddMinutes(leaveInMinutes),
        ArriveAt: Now.AddMinutes(leaveInMinutes + 21),
        IsLive: true,
        Source: ArrivalSource.Curlbus,
        ObservedAt: Now);

    private sealed class Fixture
    {
        public Fixture(Action<AppConfig>? configure = null)
        {
            Settings = new MemorySettings();
            configure?.Invoke(Settings.Config);
            Places = new SavedPlaceCollection(Settings);
            Places.Items.Add(new SavedPlace { Name = "Home", StopCode = "100" });
            Places.Items.Add(new SavedPlace { Name = "Work", StopCode = "200" });
            Places.SyncToConfig();
            Clock = new FixedClock(Now);
            Location = new FakeLocation();
            Planner = new FakeTripPlanner();
            History = new FakeHistory();
            Reminder = new FakeReminder();
            Notifier = new RecordingNotifier();
            Vm = new TripPlannerViewModel(
                Settings, Places, new TemplateLocalizer(), Planner, new FakeCatalog(HomeStop, WorkStop), Location, History,
                Reminder, Notifier, Clock, new ImmediateDispatcher(), NullLogger<TripPlannerViewModel>.Instance)
            {
                DebounceDelay = TimeSpan.Zero
            };
        }

        public MemorySettings Settings { get; }
        public SavedPlaceCollection Places { get; }
        public FixedClock Clock { get; }
        public FakeLocation Location { get; }
        public FakeTripPlanner Planner { get; }
        public FakeHistory History { get; }
        public FakeReminder Reminder { get; }
        public RecordingNotifier Notifier { get; }
        public TripPlannerViewModel Vm { get; }

        public LocationChoice Choice(string name) => Vm.Choices.First(choice => choice.DisplayName == name);
    }
}
