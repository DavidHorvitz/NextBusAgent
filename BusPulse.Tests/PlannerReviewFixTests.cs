using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.Calendar;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>Regression tests for the trip planner review findings.</summary>
public sealed class PlannerReviewFixTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop HomeStop = new("100", "Home stop", "City", 32.1000, 34.8000, Id: 1);
    private static readonly Stop WorkStop = new("200", "Work stop", "City", 32.1100, 34.8000, Id: 2);

    private sealed class Fixture
    {
        public Fixture(ITripPlannerService? planner = null)
        {
            var settings = new MemorySettings();
            var places = new SavedPlaceCollection(settings);
            places.Items.Add(new SavedPlace { Name = "Home", StopCode = "100" });
            places.Items.Add(new SavedPlace { Name = "Work", StopCode = "200" });
            places.SyncToConfig();
            Calendar = new FakeCalendar { IsConnected = true };
            FakePlanner = new FakeTripPlanner();
            Vm = new TripPlannerViewModel(
                settings, places, new TemplateLocalizer(), planner ?? FakePlanner, new FakeCatalog(HomeStop, WorkStop),
                new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.09, 34.80) }, new FakeHistory(),
                new FakeReminder(), new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(),
                NullLogger<TripPlannerViewModel>.Instance, Calendar)
            {
                DebounceDelay = TimeSpan.Zero
            };
        }

        public FakeCalendar Calendar { get; }
        public FakeTripPlanner FakePlanner { get; }
        public TripPlannerViewModel Vm { get; }
    }

    [Fact]
    public void Calendar_event_more_than_24_hours_ahead_is_not_suggested()
    {
        var places = new[] { new SavedPlace { Name = "Work", StopCode = "200" } };
        var tomorrowAfternoon = new CalendarEvent("e1", "Late", Now.AddHours(29), "Work");

        Assert.Empty(CalendarTripSuggester.Suggest([tomorrowAfternoon], places, Now));
    }

    [Fact]
    public async Task Calendar_event_tomorrow_within_24_hours_is_planned_for_its_own_instant()
    {
        var f = new Fixture();
        var start = Now.AddHours(23);
        f.Calendar.Events.Add(new CalendarEvent("e1", "Early", start, "Work"));
        await f.Vm.LoadCalendarSuggestionsAsync();

        await f.Vm.UseCalendarSuggestionCommand.ExecuteAsync(Assert.Single(f.Vm.CalendarSuggestions));

        Assert.Equal(start, f.FakePlanner.LastRequest!.TargetTime);
    }

    [Fact]
    public async Task FindTrips_RecomputesStaleSuggestionsBeforePicking()
    {
        var f = new Fixture();
        f.Vm.DestinationQuery = "Wo";
        await f.Vm.UpdateSuggestionsAsync();
        Assert.Equal("Work", f.Vm.Suggestions[0].DisplayName);

        f.Vm.DebounceDelay = TimeSpan.FromHours(1);
        f.Vm.DestinationQuery = "Ho";

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal("Home", f.Vm.SelectedDestination?.DisplayName);
    }

    [Fact]
    public async Task SlowRefresh_DoesNotOverwriteANewerPlan()
    {
        var blocked = new TaskCompletionSource<TripPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedPlanner(new TripPlan(TripPlanStatus.Ok, [SmartAlertEngineTests.Option(10) with { RideId = 2, Line = "SECOND" }]));
        var g = new Fixture(gated);
        g.Vm.SetDestination(g.Vm.Choices.First(c => c.Place?.Name == "Work"));
        gated.Gate = null;
        await g.Vm.PlanCommand.ExecuteAsync(null);

        gated.Gate = blocked.Task;
        var refresh = g.Vm.RefreshAsync();
        gated.Gate = null;
        g.Vm.SetDestination(g.Vm.Choices.First(c => c.Place?.Name == "Home"));
        await g.Vm.PlanCommand.ExecuteAsync(null);
        Assert.Equal("SECOND", Assert.Single(g.Vm.Options).Option.Line);

        blocked.SetResult(new TripPlan(TripPlanStatus.Ok, [SmartAlertEngineTests.Option(30) with { RideId = 3, Line = "STALE" }]));
        await refresh;

        Assert.Equal("SECOND", Assert.Single(g.Vm.Options).Option.Line);
    }

    private sealed class GatedPlanner(TripPlan result) : ITripPlannerService
    {
        public Task<TripPlan>? Gate { get; set; }

        public Task<TripPlan> PlanAsync(TripRequest request, CancellationToken cancellationToken = default) =>
            Gate ?? Task.FromResult(result);
    }
}
