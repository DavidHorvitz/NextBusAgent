using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>Text captured while one language was active must follow a language change.</summary>
public sealed class LanguageRefreshTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");

    [Fact]
    public void TripPlanner_MyLocationAndHint_FollowTheLanguage()
    {
        var (vm, localizer) = CreatePlanner();
        Assert.Equal("MyLocation:en", vm.MyLocation.DisplayName);
        Assert.Equal("PlanHint:en", vm.StatusText);

        localizer.SetLanguage("he");

        Assert.Equal("MyLocation:he", vm.MyLocation.DisplayName);
        Assert.Equal("PlanHint:he", vm.StatusText);
        Assert.Equal("MyLocation:he", vm.SelectedOrigin.DisplayName);
    }

    [Fact]
    public async Task TripPlanner_AMessageFromAnAction_IsNotOverwrittenByTheHint()
    {
        var (vm, localizer) = CreatePlanner();
        await vm.PlanCommand.ExecuteAsync(null);
        Assert.Equal("SetDestinationFirst:en", vm.StatusText);

        localizer.SetLanguage("he");

        Assert.Equal("SetDestinationFirst:en", vm.StatusText);
    }

    [Fact]
    public void Lines_MyLocationAndHint_FollowTheLanguage()
    {
        var localizer = new LanguageAwareLocalizer();
        var settings = new MemorySettings();
        var vm = new LinesViewModel(
            settings, new SavedPlaceCollection(settings), localizer, new FakeCatalog(), new FakeLocation(),
            new LinesViewModelTests.FakeSchedule(), new FixedClock(Now), NullLogger<LinesViewModel>.Instance);
        Assert.Equal("LinesHint:en", vm.StatusText);

        localizer.SetLanguage("he");

        Assert.Equal("LinesHint:he", vm.StatusText);
        Assert.Equal("MyLocation:he", vm.MyLocation.DisplayName);
        Assert.Equal("LinePin:he", vm.PinText);
    }

    [Fact]
    public void Settings_DefaultPlaceName_FollowsUntilTheUserEditsIt()
    {
        var localizer = new LanguageAwareLocalizer();
        var settings = new MemorySettings();
        var vm = new SettingsViewModel(
            settings, new SavedPlaceCollection(settings), localizer, [], new ShellAndSettingsTests.FakeSchedule(),
            new FakeHistory(), new RecordingNotifier(), new FakeCalendar());
        Assert.Equal("HomeName:en", vm.NewPlaceName);

        localizer.SetLanguage("he");
        Assert.Equal("HomeName:he", vm.NewPlaceName);

        vm.NewPlaceName = "My own name";
        localizer.SetLanguage("en");
        Assert.Equal("My own name", vm.NewPlaceName);
    }

    [Fact]
    public void Settings_ThemeNames_FollowTheLanguage()
    {
        var localizer = new LanguageAwareLocalizer();
        var settings = new MemorySettings();
        var vm = new SettingsViewModel(
            settings, new SavedPlaceCollection(settings), localizer, [], new ShellAndSettingsTests.FakeSchedule(),
            new FakeHistory(), new RecordingNotifier(), new FakeCalendar());
        var dark = vm.Themes.Single(theme => theme.Code == "dark");
        Assert.Equal("ThemeDark:en", dark.Name);

        localizer.SetLanguage("he");

        Assert.Equal("ThemeDark:he", dark.Name);
        Assert.Equal("ThemeSystem:he", vm.Themes.Single(theme => theme.Code == "system").Name);
    }

    [Fact]
    public void Settings_CalendarStatus_FollowsTheLanguage()
    {
        var localizer = new LanguageAwareLocalizer();
        var settings = new MemorySettings();
        var vm = new SettingsViewModel(
            settings, new SavedPlaceCollection(settings), localizer, [], new ShellAndSettingsTests.FakeSchedule(),
            new FakeHistory(), new RecordingNotifier(), new FakeCalendar { IsConfigured = false });
        Assert.Equal("CalendarNotConfigured:en", vm.CalendarStatus);

        localizer.SetLanguage("he");

        Assert.Equal("CalendarNotConfigured:he", vm.CalendarStatus);
    }

    [Fact]
    public void Stops_EmptyMessage_FollowsTheLanguage()
    {
        var localizer = new LanguageAwareLocalizer();
        var settings = new MemorySettings();
        var vm = new StopsViewModel(
            settings, new FakeLocation(), new FakeCatalog(), new NullAggregator(), new SavedPlaceCollection(settings), localizer,
            new FixedClock(Now), new ImmediateDispatcher(), NullLogger<StopsViewModel>.Instance);

        localizer.SetLanguage("he");

        Assert.Equal("StopsEmpty:he", vm.StatusText);
        Assert.Equal("StopsEmpty:he", vm.EmptyMessage);
    }

    private static (TripPlannerViewModel Vm, LanguageAwareLocalizer Localizer) CreatePlanner()
    {
        var localizer = new LanguageAwareLocalizer();
        var settings = new MemorySettings();
        var vm = new TripPlannerViewModel(
            settings, new SavedPlaceCollection(settings), localizer, new FakeTripPlanner(), new FakeCatalog(), new FakeLocation(),
            new FakeHistory(), new FakeReminder(), new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(),
            NullLogger<TripPlannerViewModel>.Instance, new FakeCalendar());
        return (vm, localizer);
    }

    private sealed class NullAggregator : BusPulse.Services.LiveData.ILiveDataAggregator
    {
        public Task<BusPulse.Services.LiveData.AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BusPulse.Services.LiveData.AggregatedArrivals([], new Dictionary<BusPulse.Services.LiveData.ArrivalSource, BusPulse.Services.LiveData.ProviderStatus>()));
    }
}
