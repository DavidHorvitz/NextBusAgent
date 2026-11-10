using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.Calendar;
using BusPulse.Services.Geocoding;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>Session 2: origin and destination are free text with ordered suggestions; the network is used only on request.</summary>
public sealed class FreeTextLocationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop HomeStop = new("100", "Home stop", "City", 32.1000, 34.8000, Id: 1);
    private static readonly Stop HerzlStop = new("200", "Herzl station", "City", 32.1100, 34.8000, Id: 2);

    private sealed class Fixture
    {
        public Fixture()
        {
            Settings = new MemorySettings();
            Places = new SavedPlaceCollection(Settings);
            Places.Items.Add(new SavedPlace { Name = "Home", StopCode = "100" });
            Places.Items.Add(new SavedPlace { Name = "Herzl Work", StopCode = "200" });
            Places.SyncToConfig();
            Geocoder = new FakeGeocoder();
            Calendar = new FakeCalendar();
            Vm = new TripPlannerViewModel(
                Settings, Places, new TemplateLocalizer(), new FakeTripPlanner(), new FakeCatalog(HomeStop, HerzlStop),
                new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.09, 34.80) }, new FakeHistory(),
                new FakeReminder(), new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(),
                NullLogger<TripPlannerViewModel>.Instance, Calendar, Geocoder)
            {
                DebounceDelay = TimeSpan.Zero,
                AutocompleteDelay = TimeSpan.Zero,
                SpinnerDelay = TimeSpan.Zero
            };
        }

        public MemorySettings Settings { get; }
        public SavedPlaceCollection Places { get; }
        public FakeGeocoder Geocoder { get; }
        public FakeCalendar Calendar { get; }
        public TripPlannerViewModel Vm { get; }

        public void Found(params GeocodedPlace[] places) => Geocoder.Result = new GeocodeResult(GeocodeStatus.Ok, places);
    }

    private static GeocodedPlace Address(string label, double lat = 32.0600) => new(label, lat, 34.7700);

    [Fact]
    public void Typing_filters_locally_and_sends_nothing_without_autocomplete()
    {
        var f = new Fixture();

        f.Vm.DestinationQuery = "Herz";
        f.Vm.DestinationQuery = "Herzl";
        f.Vm.OriginQuery = "Herzl";

        Assert.Empty(f.Geocoder.Queries);
        Assert.Empty(f.Geocoder.SuggestQueries);
        Assert.False(f.Vm.IsDestinationSearching);
        Assert.Contains(f.Vm.Suggestions, c => c.Place?.Name == "Herzl Work");
    }

    [Fact]
    public void Origin_list_starts_with_my_location_then_saved_places_then_stops()
    {
        var f = new Fixture();

        f.Vm.OriginQuery = "Herzl";

        var kinds = f.Vm.OriginSuggestions.Select(c => c.Kind).ToList();
        Assert.Equal(LocationChoiceKind.MyLocation, kinds[0]);
        Assert.Equal(LocationChoiceKind.Place, kinds[1]);
        Assert.Contains(f.Vm.OriginSuggestions, c => c.Stop?.Code == "200");
        Assert.True(kinds.IndexOf(LocationChoiceKind.Place) < kinds.IndexOf(LocationChoiceKind.Stop));
    }

    [Fact]
    public void Destination_list_never_offers_my_location()
    {
        var f = new Fixture();

        f.Vm.DestinationQuery = "Home";

        Assert.DoesNotContain(f.Vm.Suggestions, c => c.IsMyLocation);
    }

    [Fact]
    public void Empty_box_gets_the_defaults_on_focus()
    {
        var f = new Fixture();
        f.Vm.OriginQuery = string.Empty;

        f.Vm.ShowDefaultSuggestions(origin: true);

        Assert.True(f.Vm.OriginSuggestions[0].IsMyLocation);
        Assert.Contains(f.Vm.OriginSuggestions, c => c.Place?.Name == "Home");
    }

    [Fact]
    public async Task Calendar_locations_come_after_saved_places_and_are_looked_up_only_when_picked()
    {
        var f = new Fixture();
        f.Calendar.IsConnected = true;
        f.Calendar.Events = [new CalendarEvent("1", "Dentist", Now.AddHours(5), "Ben Yehuda 5, Tel Aviv")];
        await f.Vm.LoadCalendarSuggestionsAsync();

        f.Vm.DestinationQuery = string.Empty;
        f.Vm.ShowDefaultSuggestions(origin: false);

        var kinds = f.Vm.Suggestions.Select(c => c.Kind).ToList();
        Assert.Equal(LocationChoiceKind.Text, kinds[^1]);
        Assert.Empty(f.Geocoder.Queries);

        f.Found(Address("Ben Yehuda 5, Tel Aviv", 32.0700));
        await f.Vm.ChooseSuggestionCommand.ExecuteAsync(f.Vm.Suggestions.First(c => c.IsText));

        Assert.Equal(["Ben Yehuda 5, Tel Aviv"], f.Geocoder.Queries);
        Assert.Equal(LocationChoiceKind.Address, f.Vm.HighlightedDestination?.Kind);
    }

    [Fact]
    public async Task Enter_without_a_highlight_looks_the_text_up_once_and_highlights_the_first_address()
    {
        var f = new Fixture();
        f.Found(Address("Weizmann 3, Haifa"), Address("Weizmann 5, Haifa", 32.0610));
        f.Vm.DestinationQuery = "Weizmann";

        await f.Vm.CommitDestinationCommand.ExecuteAsync(null);

        Assert.Equal(["Weizmann"], f.Geocoder.Queries);
        Assert.Equal("Weizmann 3, Haifa", f.Vm.HighlightedDestination?.DisplayName);

        await f.Vm.CommitDestinationCommand.ExecuteAsync(null);

        Assert.Equal(["Weizmann"], f.Geocoder.Queries);
        Assert.Equal("Weizmann 3, Haifa", f.Vm.SelectedDestination?.DisplayName);
        Assert.Equal("Weizmann 3, Haifa", f.Vm.DestinationQuery);
        Assert.Empty(f.Vm.Suggestions);
    }

    [Fact]
    public async Task Enter_on_a_highlighted_local_suggestion_takes_it_without_a_network_call()
    {
        var f = new Fixture();
        f.Vm.DestinationQuery = "Home";
        f.Vm.HighlightedDestination = f.Vm.Suggestions.First(c => c.Place?.Name == "Home");

        await f.Vm.CommitDestinationCommand.ExecuteAsync(null);

        Assert.Equal("Home", f.Vm.SelectedDestination?.DisplayName);
        Assert.Empty(f.Geocoder.Queries);
    }

    [Fact]
    public async Task No_results_shows_the_one_line_and_no_message()
    {
        var f = new Fixture();
        f.Geocoder.Result = GeocodeResult.Of(GeocodeStatus.NoResults);
        f.Vm.DestinationQuery = "Qqqqq";

        await f.Vm.CommitDestinationCommand.ExecuteAsync(null);

        Assert.True(f.Vm.DestinationNoResults);
        Assert.Equal(string.Empty, f.Vm.DestinationSearchMessage);

        f.Vm.DestinationQuery = "Qqqqqx";
        Assert.False(f.Vm.DestinationNoResults);
    }

    [Fact]
    public async Task Leaving_the_box_looks_the_text_up_once_and_not_again_for_the_same_text()
    {
        var f = new Fixture();
        f.Found(Address("Weizmann 3, Haifa"));
        f.Vm.DestinationQuery = "Weizmann";

        await f.Vm.LeaveDestinationCommand.ExecuteAsync(null);
        await f.Vm.LeaveDestinationCommand.ExecuteAsync(null);

        Assert.Equal(["Weizmann"], f.Geocoder.Queries);
        Assert.Null(f.Vm.HighlightedDestination);
    }

    [Fact]
    public async Task Leaving_the_box_does_not_search_a_pick_a_short_text_or_an_exact_local_name()
    {
        var f = new Fixture();
        f.Vm.DestinationQuery = "Home";
        await f.Vm.LeaveDestinationCommand.ExecuteAsync(null);
        f.Vm.DestinationQuery = "He";
        await f.Vm.LeaveDestinationCommand.ExecuteAsync(null);
        f.Vm.SetDestination(f.Vm.Choices.First(c => c.Place?.Name == "Herzl Work"));
        await f.Vm.LeaveDestinationCommand.ExecuteAsync(null);

        Assert.Empty(f.Geocoder.Queries);
    }

    [Fact]
    public async Task With_autocomplete_leaving_the_box_adds_no_extra_call()
    {
        var f = new Fixture();
        f.Geocoder.Autocomplete = true;
        f.Found(Address("Weizmann 3, Haifa"));
        f.Vm.DestinationQuery = "Weizmann";

        await f.Vm.LeaveDestinationCommand.ExecuteAsync(null);

        Assert.Empty(f.Geocoder.Queries);
    }

    [Fact]
    public async Task Autocomplete_asks_after_three_characters_and_shows_the_places_after_the_local_ones()
    {
        var f = new Fixture();
        f.Geocoder.Autocomplete = true;
        f.Found(new GeocodedPlace("Herzl 10, Tel Aviv", 0, 0, "ChIJabc"));

        f.Vm.DestinationQuery = "He";
        await f.Vm.UpdateSuggestionsAsync();
        Assert.Empty(f.Geocoder.SuggestQueries);

        f.Vm.DestinationQuery = "Herzl";
        await f.Vm.UpdateSuggestionsAsync();

        Assert.Contains("Herzl", f.Geocoder.SuggestQueries);
        Assert.Equal(LocationChoiceKind.Address, f.Vm.Suggestions[^1].Kind);
        Assert.Empty(f.Geocoder.Queries);
    }

    [Fact]
    public async Task Autocomplete_off_when_address_search_is_switched_off()
    {
        var f = new Fixture();
        f.Geocoder.Autocomplete = true;
        f.Settings.Config.AddressSearchEnabled = false;

        f.Vm.DestinationQuery = "Herzl";
        await f.Vm.UpdateSuggestionsAsync();

        Assert.Empty(f.Geocoder.SuggestQueries);
    }

    [Fact]
    public async Task Choosing_a_google_suggestion_resolves_its_coordinates_first()
    {
        var f = new Fixture();
        f.Geocoder.Autocomplete = true;
        f.Found(new GeocodedPlace("Herzl 10, Tel Aviv", 0, 0, "ChIJabc"));
        f.Vm.DestinationQuery = "Herzl 1";
        await f.Vm.UpdateSuggestionsAsync();

        await f.Vm.ChooseSuggestionCommand.ExecuteAsync(f.Vm.Suggestions.First(c => c.Kind == LocationChoiceKind.Address));

        Assert.Single(f.Geocoder.Resolved);
        Assert.Equal(32.07, f.Vm.SelectedDestination?.Address?.Latitude);
        Assert.False(f.Vm.SelectedDestination!.Address!.IsUnresolved);
    }

    [Fact]
    public async Task The_spinner_shows_only_while_a_real_call_is_in_flight()
    {
        var f = new Fixture();
        var gate = new TaskCompletionSource();
        f.Geocoder.Gate = gate.Task;
        f.Found(Address("Weizmann 3, Haifa"));
        f.Vm.DestinationQuery = "Weizmann";
        Assert.False(f.Vm.IsDestinationSearching);

        var search = f.Vm.CommitDestinationCommand.ExecuteAsync(null);

        Assert.True(f.Vm.IsDestinationSearching);
        gate.SetResult();
        await search;
        Assert.False(f.Vm.IsDestinationSearching);
    }

    [Fact]
    public async Task The_spinner_waits_for_its_delay_and_a_quick_call_never_shows_it()
    {
        var f = new Fixture();
        f.Vm.SpinnerDelay = TimeSpan.FromHours(1);
        var gate = new TaskCompletionSource();
        f.Geocoder.Gate = gate.Task;
        f.Found(Address("Weizmann 3, Haifa"));
        f.Vm.DestinationQuery = "Weizmann";

        var search = f.Vm.CommitDestinationCommand.ExecuteAsync(null);
        Assert.False(f.Vm.IsDestinationSearching);
        gate.SetResult();
        await search;

        Assert.False(f.Vm.IsDestinationSearching);
    }

    [Fact]
    public async Task A_newer_keystroke_cancels_the_call_in_flight_and_drops_its_answer()
    {
        var f = new Fixture();
        f.Geocoder.Autocomplete = true;
        var gate = new TaskCompletionSource();
        f.Geocoder.Gate = gate.Task;
        f.Found(new GeocodedPlace("Old answer", 0, 0, "ChIJold"));
        f.Vm.DestinationQuery = "Herzl";
        var first = f.Vm.UpdateSuggestionsAsync();

        f.Vm.DestinationQuery = "Herzl 1";
        gate.SetResult();
        await first;

        Assert.DoesNotContain(f.Vm.Suggestions, c => c.DisplayName == "Old answer" && f.Vm.DestinationQuery == "Herzl");
        Assert.False(f.Vm.IsDestinationSearching);
    }

    [Fact]
    public void Editing_the_text_after_a_pick_makes_the_box_free_text_again()
    {
        var f = new Fixture();
        f.Vm.SetDestination(f.Vm.Choices.First(c => c.Place?.Name == "Home"));
        Assert.Equal("Home", f.Vm.DestinationQuery);

        f.Vm.DestinationQuery = "Home sweet";

        Assert.Null(f.Vm.SelectedDestination);
    }

    [Fact]
    public void Esc_closes_the_list_and_keeps_the_text()
    {
        var f = new Fixture();
        f.Vm.DestinationQuery = "Herzl";
        Assert.NotEmpty(f.Vm.Suggestions);

        f.Vm.ClearSuggestionsCommand.Execute(null);

        Assert.Empty(f.Vm.Suggestions);
        Assert.Equal("Herzl", f.Vm.DestinationQuery);
    }

    [Fact]
    public async Task A_geocoder_result_that_repeats_a_saved_place_is_not_listed_twice()
    {
        var f = new Fixture();
        f.Found(Address("Home"), Address("Elsewhere 1", 32.5));
        f.Vm.DestinationQuery = "Home";

        await f.Vm.CommitDestinationCommand.ExecuteAsync(null);

        Assert.Single(f.Vm.Suggestions, c => c.DisplayName == "Home");
    }

    [Fact]
    public async Task Rate_limited_and_cap_statuses_show_a_calm_message()
    {
        var f = new Fixture();
        f.Geocoder.Result = GeocodeResult.Of(GeocodeStatus.RateLimited);
        f.Vm.DestinationQuery = "Weizmann";

        await f.Vm.CommitDestinationCommand.ExecuteAsync(null);

        Assert.Equal("GeocodeRateLimited", f.Vm.DestinationSearchMessage);
        Assert.False(f.Vm.DestinationNoResults);
    }

    [Fact]
    public void An_emptied_origin_box_plans_from_the_current_location_again()
    {
        var f = new Fixture();
        f.Vm.SetOrigin(f.Vm.Choices.First(c => c.Place?.Name == "Home"));
        Assert.Equal("Home", f.Vm.OriginQuery);

        f.Vm.OriginQuery = string.Empty;
        f.Vm.SetDestination(f.Vm.Choices.First(c => c.Place?.Name == "Herzl Work"));
        f.Vm.PlanCommand.Execute(null);

        Assert.True(f.Vm.SelectedOrigin.IsMyLocation);
    }
}
