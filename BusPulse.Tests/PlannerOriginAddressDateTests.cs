using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.Geocoding;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.Xml.Linq;
using Xunit;

namespace BusPulse.Tests;

/// <summary>Origin search, address search, saving places and date selection in the trip planner.</summary>
public sealed class PlannerOriginAddressDateTests
{
    // 13:00 in Israel (UTC+3 until 2026-10-25), so 08:00 has passed and 15:00 has not.
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop HomeStop = new("100", "Home stop", "City", 32.1000, 34.8000, Id: 1);
    private static readonly Stop WorkStop = new("200", "Work stop", "City", 32.1100, 34.8000, Id: 2);

    private static GeocodedPlace Herzl => new("Herzl 10, Tel Aviv", 32.0600, 34.7700);

    private sealed class Fixture
    {
        public Fixture()
        {
            Settings = new MemorySettings();
            Places = new SavedPlaceCollection(Settings);
            Places.Items.Add(new SavedPlace { Name = "Home", StopCode = "100" });
            Places.Items.Add(new SavedPlace { Name = "Work", StopCode = "200" });
            Places.SyncToConfig();
            Planner = new FakeTripPlanner();
            Geocoder = new FakeGeocoder();
            History = new FakeHistory();
            Vm = new TripPlannerViewModel(
                Settings, Places, new TemplateLocalizer(), Planner, new FakeCatalog(HomeStop, WorkStop),
                new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.09, 34.80) }, History,
                new FakeReminder(), new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(),
                NullLogger<TripPlannerViewModel>.Instance, new FakeCalendar(), Geocoder)
            {
                DebounceDelay = TimeSpan.Zero
            };
        }

        public MemorySettings Settings { get; }
        public SavedPlaceCollection Places { get; }
        public FakeTripPlanner Planner { get; }
        public FakeGeocoder Geocoder { get; }
        public FakeHistory History { get; }
        public TripPlannerViewModel Vm { get; }

        public void FoundAddresses(int count = 1)
        {
            Geocoder.Result = new GeocodeResult(
                GeocodeStatus.Ok,
                Enumerable.Range(0, count).Select(i => new GeocodedPlace($"Herzl {10 + i}, Tel Aviv", 32.0600 + (i * 0.001), 34.7700)).ToArray());
        }

        public LocationChoice Choice(string placeName) => Vm.Choices.First(c => c.Place?.Name == placeName);
    }

    // ---- origin search ----

    [Fact]
    public async Task Origin_search_suggests_saved_places_and_stops_and_plans_from_the_choice()
    {
        var f = new Fixture();

        f.Vm.OriginQuery = "Work";

        Assert.Contains(f.Vm.OriginSuggestions, c => c.Place?.Name == "Work");
        Assert.Contains(f.Vm.OriginSuggestions, c => c.Stop?.Code == "200");
        f.Vm.ChooseOriginSuggestionCommand.Execute(f.Vm.OriginSuggestions.First(c => c.Place?.Name == "Work"));
        Assert.Equal("Work", f.Vm.SelectedOrigin.DisplayName);
        Assert.Equal("Work", f.Vm.OriginQuery);
        Assert.Empty(f.Vm.OriginSuggestions);

        f.Vm.SetDestination(f.Choice("Home"));
        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal(WorkStop.Latitude, f.Planner.LastRequest!.OriginLatitude);
        Assert.Equal(HomeStop.Latitude, f.Planner.LastRequest.DestinationLatitude);
    }

    [Fact]
    public void Origin_defaults_to_my_location()
    {
        Assert.True(new Fixture().Vm.SelectedOrigin.IsMyLocation);
    }

    [Fact]
    public async Task Typed_origin_without_picking_uses_the_best_match_when_planning()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Choice("Home"));
        f.Vm.OriginQuery = "Work";

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal(WorkStop.Latitude, f.Planner.LastRequest!.OriginLatitude);
    }

    // ---- address search ----

    [Fact]
    public async Task Typing_never_calls_the_geocoder()
    {
        var f = new Fixture();
        f.FoundAddresses(3);

        f.Vm.DestinationQuery = "Herzl 10";
        f.Vm.OriginQuery = "Dizengoff 50";
        await f.Vm.UpdateSuggestionsAsync();
        await f.Vm.UpdateOriginSuggestionsAsync();

        Assert.Empty(f.Geocoder.Queries);
        Assert.DoesNotContain(f.Vm.Suggestions, c => c.Kind == LocationChoiceKind.Address);
        Assert.DoesNotContain(f.Vm.OriginSuggestions, c => c.Kind == LocationChoiceKind.Address);
    }

    [Fact]
    public async Task Refresh_ticks_and_planning_do_not_call_the_geocoder()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Choice("Home"));
        await f.Vm.PlanCommand.ExecuteAsync(null);

        await f.Vm.RefreshAsync();
        f.Vm.Tick();

        Assert.Empty(f.Geocoder.Queries);
    }

    [Fact]
    public async Task Search_address_command_calls_the_geocoder_once_and_shows_at_most_five_addresses()
    {
        var f = new Fixture();
        f.FoundAddresses(7);
        f.Vm.DestinationQuery = "Herzl";

        await f.Vm.SearchDestinationAddressCommand.ExecuteAsync(null);

        Assert.Equal(["Herzl"], f.Geocoder.Queries);
        Assert.Equal(5, f.Vm.Suggestions.Count(c => c.Kind == LocationChoiceKind.Address));
        Assert.Equal(string.Empty, f.Vm.DestinationSearchMessage);
        Assert.Equal(LocationChoiceKind.Address, f.Vm.HighlightedDestination?.Kind);
    }

    [Fact]
    public async Task Origin_search_address_command_fills_the_origin_list_only()
    {
        var f = new Fixture();
        f.FoundAddresses(2);
        f.Vm.OriginQuery = "Herzl";

        await f.Vm.SearchOriginAddressCommand.ExecuteAsync(null);

        Assert.Equal(2, f.Vm.OriginSuggestions.Count(c => c.Kind == LocationChoiceKind.Address));
        Assert.Empty(f.Vm.Suggestions);
    }

    [Fact]
    public async Task Chosen_address_becomes_a_coordinate_destination_and_origin_for_the_planner()
    {
        var f = new Fixture();
        f.FoundAddresses(1);
        f.Vm.DestinationQuery = "Herzl";
        await f.Vm.SearchDestinationAddressCommand.ExecuteAsync(null);
        var address = f.Vm.Suggestions.Single(c => c.Kind == LocationChoiceKind.Address);
        f.Vm.ChooseSuggestionCommand.Execute(address);

        f.Vm.OriginQuery = "Herzl";
        await f.Vm.SearchOriginAddressCommand.ExecuteAsync(null);
        f.Vm.ChooseOriginSuggestionCommand.Execute(f.Vm.OriginSuggestions.Single(c => c.Kind == LocationChoiceKind.Address));
        // Same coordinates on both ends are rejected, so move the origin to the work stop first.
        f.Vm.SetOrigin(f.Choice("Work"));
        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal("Herzl 10, Tel Aviv", f.Vm.SelectedDestination!.DisplayName);
        Assert.Equal(32.0600, f.Planner.LastRequest!.DestinationLatitude);
        Assert.Equal(34.7700, f.Planner.LastRequest.DestinationLongitude);

        f.Vm.SetOrigin(address);
        f.Vm.SetDestination(f.Choice("Home"));
        await f.Vm.PlanCommand.ExecuteAsync(null);
        Assert.Equal(32.0600, f.Planner.LastRequest!.OriginLatitude);
        Assert.Equal(34.7700, f.Planner.LastRequest.OriginLongitude);
    }

    [Fact]
    public async Task Addresses_are_never_written_to_the_history()
    {
        var f = new Fixture();
        f.FoundAddresses(1);
        f.Vm.DestinationQuery = "Herzl";
        await f.Vm.SearchDestinationAddressCommand.ExecuteAsync(null);
        f.Vm.ChooseSuggestionCommand.Execute(f.Vm.Suggestions.Single(c => c.Kind == LocationChoiceKind.Address));

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Empty(f.History.Entries);
        Assert.Null(f.Settings.Config.DestinationPlaceId);
    }

    [Fact]
    public async Task A_saved_address_place_is_not_written_to_the_history_either()
    {
        var f = new Fixture();
        f.Vm.SetDestination(new LocationChoice(Herzl.Label, address: Herzl));
        await f.Vm.SaveDestinationCommand.ExecuteAsync(null);
        Assert.NotNull(f.Vm.SelectedDestination?.Place);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Empty(f.History.Entries);
    }

    [Theory]
    [InlineData(GeocodeStatus.Disabled, "GeocodeDisabled")]
    [InlineData(GeocodeStatus.Unavailable, "GeocodeUnavailable")]
    [InlineData(GeocodeStatus.RateLimited, "GeocodeRateLimited")]
    [InlineData(GeocodeStatus.DailyCapReached, "GeocodeDailyCap")]
    [InlineData(GeocodeStatus.TooShort, "GeocodeTooShort")]
    public async Task Geocode_problems_show_a_localized_message_and_no_suggestions(GeocodeStatus status, string key)
    {
        var f = new Fixture();
        f.Geocoder.Result = GeocodeResult.Of(status);
        f.Vm.DestinationQuery = "Herzl";
        f.Vm.OriginQuery = "Herzl";

        await f.Vm.SearchDestinationAddressCommand.ExecuteAsync(null);
        await f.Vm.SearchOriginAddressCommand.ExecuteAsync(null);

        Assert.Equal(key, f.Vm.DestinationSearchMessage);
        Assert.Equal(key, f.Vm.OriginSearchMessage);
        Assert.DoesNotContain(f.Vm.Suggestions, c => c.Kind == LocationChoiceKind.Address);
    }

    [Fact]
    public void Every_new_string_exists_in_both_languages()
    {
        foreach (var language in new[] { "en", "he" })
        {
            var strings = AlertTextTests.Load(language);
            foreach (var key in new[]
            {
                "OriginSearch", "SearchAddress", "SearchAddressHint", "GeocodeDisabled", "GeocodeUnavailable", "GeocodeNoResults",
                "GeocodeTooShort", "GeocodeResults", "SaveAsHome", "SaveAsWork", "PlaceNameWork", "PlaceSavedNamed", "PlaceLimitReached",
                "DateLabel", "DateToday", "DateTomorrow", "TimePassedTomorrow", "PlanNoScheduleForDate", "PlannedForDate", "HomeName",
                "SavePlace", "PlaceSaveFailed"
            })
            {
                Assert.True(strings.ContainsKey(key), $"{language}: {key}");
            }
        }
    }

    // ---- save place ----

    [Fact]
    public async Task Saving_an_address_adds_a_coordinate_place_with_a_short_name_and_selects_it()
    {
        var f = new Fixture();
        var choice = new LocationChoice(Herzl.Label, address: Herzl);
        f.Vm.SetDestination(choice);

        await f.Vm.SaveDestinationCommand.ExecuteAsync(null);

        var place = f.Places.Items.Last();
        Assert.Equal("Herzl 10", place.Name);
        Assert.Equal(32.0600, place.Latitude);
        Assert.Equal(34.7700, place.Longitude);
        Assert.Equal(string.Empty, place.StopCode);
        Assert.Equal(Herzl.Label, place.Address);
        Assert.Contains(f.Settings.Config.SavedPlaces, p => p.Id == place.Id);
        Assert.Equal(1, f.Settings.SaveCount);
        Assert.Equal(place.Id, f.Vm.SelectedDestination?.Place?.Id);
        Assert.False(f.Vm.CanSaveDestination);
    }

    [Fact]
    public async Task Saving_a_stop_keeps_its_code()
    {
        var f = new Fixture();
        f.Vm.SetOrigin(new LocationChoice("Elsewhere", stop: new Stop("555", "Elsewhere", "City", 32.2, 34.9, Id: 9)));

        await f.Vm.SaveOriginCommand.ExecuteAsync(null);

        var place = f.Places.Items.Last();
        Assert.Equal("555", place.StopCode);
        Assert.Null(place.Latitude);
        Assert.Equal("Elsewhere", place.Name);
        Assert.Equal(place.Id, f.Vm.SelectedOrigin.Place?.Id);
    }

    [Fact]
    public async Task Save_as_home_wires_the_home_slot_and_names_it_home_uniquely()
    {
        var f = new Fixture();
        f.Vm.SetDestination(new LocationChoice(Herzl.Label, address: Herzl));

        await f.Vm.SaveDestinationCommand.ExecuteAsync("home");

        var place = f.Places.Items.Last();
        Assert.Equal("HomeName", place.Name);
        Assert.Equal(place.Id, f.Settings.Config.HomePlaceId);
        Assert.True(f.Vm.HasHome);
        // A place called "Home" already exists, so the new one gets a suffix instead of a duplicate name.
        Assert.Equal(f.Places.Items.Count, f.Places.Items.Select(p => p.Name.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public async Task Save_as_work_wires_the_work_slot()
    {
        var f = new Fixture();
        f.Vm.SetOrigin(new LocationChoice(Herzl.Label, address: Herzl));

        await f.Vm.SaveOriginCommand.ExecuteAsync("work");

        Assert.Equal(f.Places.Items.Last().Id, f.Settings.Config.WorkPlaceId);
        Assert.True(f.Vm.HasWork);
    }

    [Fact]
    public async Task Saving_twice_gives_unique_names()
    {
        var f = new Fixture();
        f.Vm.SetDestination(new LocationChoice(Herzl.Label, address: Herzl));
        await f.Vm.SaveDestinationCommand.ExecuteAsync(null);
        f.Vm.SetDestination(new LocationChoice(Herzl.Label, address: Herzl));

        await f.Vm.SaveDestinationCommand.ExecuteAsync(null);

        Assert.Equal(["Herzl 10", "Herzl 10 2"], f.Places.Items.Skip(2).Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Saving_stops_at_the_thirty_place_limit()
    {
        var f = new Fixture();
        while (f.Places.Items.Count < 30)
        {
            f.Places.Items.Add(new SavedPlace { Name = $"P{f.Places.Items.Count}", StopCode = "100" });
        }

        f.Vm.SetDestination(new LocationChoice(Herzl.Label, address: Herzl));
        await f.Vm.SaveDestinationCommand.ExecuteAsync(null);

        Assert.Equal(30, f.Places.Items.Count);
        Assert.Equal("PlaceLimitReached", f.Vm.StatusText);
        Assert.Equal(0, f.Settings.SaveCount);
    }

    [Fact]
    public async Task A_failed_write_rolls_the_place_back()
    {
        var f = new Fixture();
        f.Settings.SaveSucceeds = false;
        f.Vm.SetDestination(new LocationChoice(Herzl.Label, address: Herzl));

        await f.Vm.SaveDestinationCommand.ExecuteAsync("home");

        Assert.Equal(2, f.Places.Items.Count);
        Assert.Null(f.Settings.Config.HomePlaceId);
        Assert.Equal("PlaceSaveFailed", f.Vm.StatusText);
    }

    [Fact]
    public async Task Saved_places_and_my_location_cannot_be_saved_again()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Choice("Home"));

        await f.Vm.SaveDestinationCommand.ExecuteAsync(null);
        await f.Vm.SaveOriginCommand.ExecuteAsync(null);

        Assert.False(f.Vm.CanSaveOrigin);
        Assert.False(f.Vm.CanSaveDestination);
        Assert.Equal(2, f.Places.Items.Count);
    }

    // ---- date ----

    [Fact]
    public void Now_mode_has_no_target_and_the_date_selector_follows_the_mode()
    {
        var f = new Fixture();
        Assert.False(f.Vm.IsDateSelectable);

        f.Vm.SelectedModeIndex = 1;

        Assert.True(f.Vm.IsDateSelectable);
        Assert.Equal(new DateTime(2026, 10, 20), f.Vm.MinDate);
        Assert.Equal(new DateTime(2026, 12, 19), f.Vm.MaxDate);
    }

    [Fact]
    public async Task Depart_at_tomorrow_sends_that_exact_instant()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Choice("Home"));
        f.Vm.SelectedModeIndex = 1;
        f.Vm.SelectTomorrowCommand.Execute(null);
        f.Vm.TargetTime = new TimeSpan(7, 30, 0);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        // 07:30 in Israel on 21 October is 04:30 UTC.
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T04:30:00+00:00"), f.Planner.LastRequest!.TargetTime);
        Assert.Equal(TripTimeMode.DepartAt, f.Planner.LastRequest.Mode);
        Assert.Equal(string.Empty, f.Vm.DateNotice);
    }

    [Fact]
    public async Task Arrive_by_a_date_further_ahead_resolves_that_day()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Choice("Home"));
        f.Vm.SelectedModeIndex = 2;
        f.Vm.TargetDate = new DateTime(2026, 11, 2);
        f.Vm.TargetTime = new TimeSpan(9, 0, 0);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        // 09:00 on 2 November is after the clock change, so UTC+2.
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T07:00:00+00:00"), f.Planner.LastRequest!.TargetTime);
    }

    [Fact]
    public async Task A_time_that_has_passed_today_jumps_to_tomorrow_and_shows_the_date()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Choice("Home"));
        f.Vm.SelectedModeIndex = 1;
        f.Vm.TargetTime = new TimeSpan(8, 0, 0);

        // The warning is visible before planning, never a silent change.
        Assert.True(f.Vm.HasDateNotice);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal(new DateTime(2026, 10, 21), f.Vm.TargetDate);
        Assert.True(f.Vm.HasDateNotice);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T05:00:00+00:00"), f.Planner.LastRequest!.TargetTime);
    }

    [Fact]
    public async Task A_time_later_today_stays_today_without_a_notice()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Choice("Home"));
        f.Vm.SelectedModeIndex = 1;
        f.Vm.TargetTime = new TimeSpan(15, 0, 0);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.False(f.Vm.HasDateNotice);
        Assert.Equal(new DateTime(2026, 10, 20), f.Vm.TargetDate);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T12:00:00+00:00"), f.Planner.LastRequest!.TargetTime);
    }

    [Fact]
    public async Task Dates_outside_the_allowed_range_are_clamped()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Choice("Home"));
        f.Vm.SelectedModeIndex = 1;
        f.Vm.TargetTime = new TimeSpan(15, 0, 0);
        f.Vm.TargetDate = new DateTime(2027, 6, 1);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal(new DateTime(2026, 12, 19), f.Vm.TargetDate);
    }

    [Fact]
    public async Task Now_mode_sends_no_target_even_with_a_future_date_selected()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Choice("Home"));
        f.Vm.SelectTomorrowCommand.Execute(null);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Null(f.Planner.LastRequest!.TargetTime);
    }

    [Fact]
    public async Task Calendar_event_tomorrow_shows_its_date_and_instant()
    {
        var f = new Fixture();
        var calendar = new FakeCalendar { IsConnected = true };
        var vm = new TripPlannerViewModel(
            f.Settings, f.Places, new TemplateLocalizer(), f.Planner, new FakeCatalog(HomeStop, WorkStop),
            new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.09, 34.80) }, f.History,
            new FakeReminder(), new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(),
            NullLogger<TripPlannerViewModel>.Instance, calendar, f.Geocoder);
        var start = DateTimeOffset.Parse("2026-10-21T05:15:00+00:00");
        calendar.Events.Add(new BusPulse.Services.Calendar.CalendarEvent("e1", "Early", start, "Work"));
        await vm.LoadCalendarSuggestionsAsync();

        await vm.UseCalendarSuggestionCommand.ExecuteAsync(vm.CalendarSuggestions[0]);

        Assert.Equal(new DateTime(2026, 10, 21), vm.TargetDate);
        Assert.Equal(start, f.Planner.LastRequest!.TargetTime);
    }

    [Fact]
    public async Task NoScheduleForDate_shows_the_honest_message()
    {
        var f = new Fixture();
        f.Planner.Plan = new TripPlan(TripPlanStatus.NoScheduleForDate, []);
        f.Vm.SetDestination(f.Choice("Home"));
        f.Vm.SelectedModeIndex = 1;
        f.Vm.SelectTomorrowCommand.Execute(null);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal("PlanNoScheduleForDate", f.Vm.StatusText);
        Assert.Empty(f.Vm.Options);
    }
}

public sealed class TripTimeDateTests
{
    private static readonly TimeZoneInfo Zone = TripTime.IsraelZone;

    [Theory]
    [InlineData("2026-10-24", "09:00:00")] // still summer time, UTC+3
    [InlineData("2026-10-25", "10:00:00")] // the clock went back at 02:00, UTC+2 by noon
    [InlineData("2027-03-25", "10:00:00")] // winter time, UTC+2
    [InlineData("2027-03-26", "09:00:00")] // the clock went forward at 02:00, UTC+3 by noon
    public void Noon_keeps_its_wall_clock_hour_around_the_clock_changes(string date, string utcTime)
    {
        var instant = TripTime.Resolve(DateOnly.Parse(date), TimeSpan.FromHours(12), Zone);

        Assert.Equal(DateTimeOffset.Parse($"{date}T{utcTime}+00:00"), instant);
    }

    [Fact]
    public void A_time_skipped_by_the_spring_change_moves_one_hour_later()
    {
        // 02:30 does not exist on 2027-03-26; 03:30 (UTC+3) is 00:30 UTC.
        var instant = TripTime.Resolve(new DateOnly(2027, 3, 26), new TimeSpan(2, 30, 0), Zone);

        Assert.Equal(DateTimeOffset.Parse("2027-03-26T00:30:00+00:00"), instant);
    }

    [Fact]
    public void An_ambiguous_autumn_time_resolves_to_its_first_occurrence()
    {
        var instant = TripTime.Resolve(new DateOnly(2026, 10, 25), new TimeSpan(1, 30, 0), Zone);

        Assert.Equal(DateTimeOffset.Parse("2026-10-24T22:30:00+00:00"), instant);   // first occurrence, summer offset
    }

    [Fact]
    public void An_explicit_date_never_rolls_to_another_day()
    {
        var now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");

        var instant = TripTime.Resolve(new DateOnly(2026, 10, 20), new TimeSpan(8, 0, 0), Zone);

        Assert.Equal(DateTimeOffset.Parse("2026-10-20T05:00:00+00:00"), instant);
        Assert.True(TripTime.HasPassed(now, new DateOnly(2026, 10, 20), new TimeSpan(8, 0, 0), Zone));
        Assert.False(TripTime.HasPassed(now, new DateOnly(2026, 10, 20), new TimeSpan(15, 0, 0), Zone));
    }

    [Fact]
    public void Today_is_the_israel_calendar_date_not_the_utc_one()
    {
        // 22:30 UTC on the 24th is already 01:30 on the 25th in Israel.
        var now = DateTimeOffset.Parse("2026-10-24T22:30:00+00:00");

        Assert.Equal(new DateOnly(2026, 10, 25), TripTime.TodayIn(now, Zone));
    }

    [Fact]
    public void The_time_only_overload_still_rolls_a_passed_time_to_tomorrow_across_the_change()
    {
        // 23:00 Israel time on 24 October (20:00 UTC); 09:00 has passed, so it means 25 October (UTC+2).
        var now = DateTimeOffset.Parse("2026-10-24T20:00:00+00:00");

        Assert.Equal(DateTimeOffset.Parse("2026-10-25T07:00:00+00:00"), TripTime.Resolve(now, TimeSpan.FromHours(9), Zone));
    }
}

public sealed class NoScheduleForDateServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop StopA = new("100", "Origin stop", "Bnei Brak", 32.1000, 34.8000, Id: 1);
    private static readonly Stop StopB = new("200", "Destination stop", "Tel Aviv", 32.1100, 34.8000, Id: 2);

    private static (TripPlannerService Planner, TripPlannerServiceTests.FakeRideSchedule Schedule, TripPlannerServiceTests.FakeAggregator Aggregator) Build()
    {
        var schedule = new TripPlannerServiceTests.FakeRideSchedule();
        var aggregator = new TripPlannerServiceTests.FakeAggregator();
        var planner = new TripPlannerService(new FakeCatalog(StopA, StopB), schedule, aggregator, new FixedClock(Now), NullLogger<TripPlannerService>.Instance);
        return (planner, schedule, aggregator);
    }

    private static TripRequest Request(TripTimeMode mode, DateTimeOffset target) =>
        new(32.1000, 34.8000, 32.1100, 34.8000, mode, target);

    [Theory]
    [InlineData(TripTimeMode.DepartAt)]
    [InlineData(TripTimeMode.ArriveBy)]
    public async Task A_later_date_with_no_departures_at_all_is_not_a_timetable_yet(TripTimeMode mode)
    {
        var (planner, _, _) = Build();

        var plan = await planner.PlanAsync(Request(mode, Now.AddDays(1)));

        Assert.Equal(TripPlanStatus.NoScheduleForDate, plan.Status);
        Assert.Empty(plan.Options);
    }

    [Fact]
    public async Task Today_with_no_departures_is_still_plain_no_options()
    {
        var (planner, _, _) = Build();

        var plan = await planner.PlanAsync(Request(TripTimeMode.DepartAt, Now.AddHours(2)));

        Assert.Equal(TripPlanStatus.NoOptions, plan.Status);
    }

    [Fact]
    public async Task Departures_that_exist_for_a_later_date_are_planned_and_never_live()
    {
        var (planner, schedule, aggregator) = Build();
        var departure = Now.AddDays(1);
        schedule.Departures[1] = [new RideStop(10, 1, 2, departure, "4", "Dan", "A<->B-City-1")];
        schedule.DestinationRows = [new RideStop(10, 2, 5, departure.AddMinutes(15), string.Empty, null, null)];
        aggregator.Arrivals["100"] = [new BusPulse.Services.LiveData.Arrival("100", "4", "Dan", "X", departure, true, BusPulse.Services.LiveData.ArrivalSource.Curlbus, Now)];

        var plan = await planner.PlanAsync(new TripRequest(32.1000, 34.8000, 32.1100, 34.8000, TripTimeMode.DepartAt, departure, WatchRideId: 10));

        var option = Assert.Single(plan.Options);
        Assert.False(option.IsLive);
        Assert.Equal(0, aggregator.Calls);
    }
}

public sealed class AlertTextTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");

    /// <summary>The real resource strings, so the wording the user reads is what is tested.</summary>
    internal static Dictionary<string, string> Load(string language)
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "BusPulse.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = System.IO.Path.Combine(directory.FullName, "BusPulse", "Resources", $"Strings.{language}.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Root!.Elements()
            .ToDictionary(element => (string)element.Attribute(x + "Key")!, element => element.Value);
    }

    private sealed class FileLocalizer(string language) : ILocalizer
    {
        private readonly Dictionary<string, string> _strings = Load(language);

        public event EventHandler? LanguageChanged { add { } remove { } }

        public string Get(string key) => _strings.GetValueOrDefault(key, key);

        public void SetLanguage(string value)
        {
        }
    }

    /// <summary>A trip that starts from an address 5 minutes' walk from the stop, leaving in 10 minutes.</summary>
    private static TripOption FromAddress(int leaveInMinutes, int walkMinutes, string stopName, string line)
    {
        var leaveBy = Now.AddMinutes(leaveInMinutes);
        var bus = leaveBy.AddMinutes(walkMinutes + 1);
        var stop = new Stop("100", stopName, "Tel Aviv", 32.1, 34.8, Id: 1);
        return new TripOption(
            10, line, "Dan", null, "Tel Aviv", stop, walkMinutes * 75, TimeSpan.FromMinutes(walkMinutes),
            new Stop("200", "Destination stop", "City", 32.11, 34.8, Id: 2), 0, TimeSpan.Zero,
            bus, bus.AddMinutes(15), leaveBy, bus.AddMinutes(15), false, BusPulse.Services.LiveData.ArrivalSource.Schedule, Now);
    }

    [Fact]
    public void English_leave_reminder_says_when_to_leave_and_how_long_to_walk_to_which_stop()
    {
        var engine = new BusPulse.Services.Notifications.SmartAlertEngine(new FileLocalizer("en"));

        var message = engine.Evaluate(FromAddress(10, 5, "Dizengoff Center", "77"), null, Now, leadMinutes: 10)!;

        Assert.Equal("Leave in 10 min: 5 min walk to Dizengoff Center for line 77", message.Message);
    }

    [Fact]
    public void Hebrew_leave_reminder_names_the_walk_the_stop_and_the_line()
    {
        var engine = new BusPulse.Services.Notifications.SmartAlertEngine(new FileLocalizer("he"));

        var message = engine.Evaluate(FromAddress(8, 6, "דיזנגוף סנטר", "77"), null, Now, leadMinutes: 5)!;

        Assert.Contains("8", message.Message);
        Assert.Contains("6 דק׳ הליכה", message.Message);
        Assert.Contains("דיזנגוף סנטר", message.Message);
        Assert.Contains("77", message.Message);
        Assert.Matches("[֐-׿]", message.Message);
    }

    [Fact]
    public void The_walk_comes_from_the_origin_point_so_a_far_address_means_a_longer_walk()
    {
        var engine = new BusPulse.Services.Notifications.SmartAlertEngine(new FileLocalizer("en"));
        var near = engine.Evaluate(FromAddress(9, 2, "Stop A", "5"), null, Now, 10)!;
        engine.Reset();
        var far = engine.Evaluate(FromAddress(9, 11, "Stop A", "5"), null, Now, 10)!;

        Assert.StartsWith("Leave in 9 min: 2 min walk", near.Message);
        Assert.StartsWith("Leave in 9 min: 11 min walk", far.Message);
    }

    [Fact]
    public void Both_languages_use_the_same_alert_placeholders()
    {
        var english = Load("en")["AlertWalkDetail"];
        var hebrew = Load("he")["AlertWalkDetail"];

        foreach (var placeholder in new[] { "{0}", "{3}", "{4}", "{5}" })
        {
            Assert.Contains(placeholder, english);
            Assert.Contains(placeholder, hebrew);
        }
    }
}
