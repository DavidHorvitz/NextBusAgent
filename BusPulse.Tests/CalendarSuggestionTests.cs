using System.Net.Http;
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

public class CalendarSuggestionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop HomeStop = new("100", "Home stop", "City", 32.1000, 34.8000, Id: 1);
    private static readonly Stop WorkStop = new("200", "Work stop", "City", 32.1100, 34.8000, Id: 2);

    private static (TripPlannerViewModel Vm, FakeCalendar Calendar, FakeTripPlanner Planner, SavedPlace Work) Build()
    {
        var settings = new MemorySettings();
        var places = new SavedPlaceCollection(settings);
        places.Items.Add(new SavedPlace { Name = "Home", StopCode = "100" });
        var work = new SavedPlace { Name = "Work", StopCode = "200" };
        places.Items.Add(work);
        places.SyncToConfig();
        var calendar = new FakeCalendar { IsConnected = true };
        var planner = new FakeTripPlanner();
        var location = new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.09, 34.80) };
        var vm = new TripPlannerViewModel(
            settings, places, new TemplateLocalizer(), planner, new FakeCatalog(HomeStop, WorkStop), location, new FakeHistory(),
            new FakeReminder(), new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(),
            NullLogger<TripPlannerViewModel>.Instance, calendar)
        {
            DebounceDelay = TimeSpan.Zero
        };
        return (vm, calendar, planner, work);
    }

    [Fact]
    public async Task Connected_calendar_event_at_saved_place_becomes_a_suggestion()
    {
        var (vm, calendar, _, _) = Build();
        calendar.Events.Add(new CalendarEvent("e1", "Team meeting", Now.AddHours(5), "Work"));

        await vm.LoadCalendarSuggestionsAsync();

        var item = Assert.Single(vm.CalendarSuggestions);
        Assert.Equal("Team meeting", item.Title);
        Assert.Equal("Work", item.PlaceName);
    }

    [Fact]
    public async Task Disconnected_calendar_gives_no_suggestions_and_is_not_queried()
    {
        var (vm, calendar, _, _) = Build();
        calendar.IsConnected = false;
        calendar.Events.Add(new CalendarEvent("e1", "Team meeting", Now.AddHours(5), "Work"));

        await vm.LoadCalendarSuggestionsAsync();

        Assert.Empty(vm.CalendarSuggestions);
        Assert.Equal(0, calendar.UpcomingCalls);
    }

    [Fact]
    public async Task Calendar_failure_leaves_suggestions_empty()
    {
        var (vm, calendar, _, _) = Build();
        calendar.Throw = new HttpRequestException("offline");

        await vm.LoadCalendarSuggestionsAsync();

        Assert.Empty(vm.CalendarSuggestions);
    }

    [Fact]
    public async Task Using_a_suggestion_sets_destination_arrive_by_time_and_plans()
    {
        var (vm, calendar, planner, work) = Build();
        var start = Now.AddHours(5);
        calendar.Events.Add(new CalendarEvent("e1", "Team meeting", start, "Work"));
        await vm.LoadCalendarSuggestionsAsync();

        await vm.UseCalendarSuggestionCommand.ExecuteAsync(vm.CalendarSuggestions[0]);

        Assert.Equal(work.Id, vm.SelectedDestination?.Place?.Id);
        Assert.Equal(2, vm.SelectedModeIndex);
        Assert.Equal(TimeZoneInfo.ConvertTime(start, TripTime.IsraelZone).TimeOfDay, vm.TargetTime);
        Assert.Equal(1, planner.Calls);
    }
}
