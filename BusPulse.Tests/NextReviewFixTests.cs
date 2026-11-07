using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.Calendar;
using BusPulse.Services.Geocoding;
using BusPulse.Services.Location;
using BusPulse.Services.Secrets;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;

namespace BusPulse.Tests;

/// <summary>Regression tests for the second independent review: address search plan, sticky date, failed place save, calendar client id.</summary>
public sealed class NextReviewFixTests
{
    // 09:00 in Israel (UTC+3 until 2026-10-25): 08:00 has passed, 10:00 has not.
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T06:00:00+00:00");
    private static readonly Stop HerzlStop = new("100", "Herzl stop", "Haifa", 32.8000, 35.0000, Id: 1);
    private static readonly Stop HomeStop = new("200", "Home stop", "City", 32.1000, 34.8000, Id: 2);

    private sealed class PlannerFixture
    {
        public PlannerFixture()
        {
            Settings = new MemorySettings();
            var places = new SavedPlaceCollection(Settings);
            places.Items.Add(new SavedPlace { Name = "Home", StopCode = "200" });
            places.SyncToConfig();
            Planner = new FakeTripPlanner();
            Geocoder = new FakeGeocoder
            {
                Result = new GeocodeResult(GeocodeStatus.Ok, [new GeocodedPlace("Herzl 10, Tel Aviv", 32.0600, 34.7700)])
            };
            Vm = new TripPlannerViewModel(
                Settings, places, new TemplateLocalizer(), Planner, new FakeCatalog(HerzlStop, HomeStop),
                new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.09, 34.80) }, new FakeHistory(),
                new FakeReminder(), new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(),
                NullLogger<TripPlannerViewModel>.Instance, new FakeCalendar(), Geocoder)
            {
                DebounceDelay = TimeSpan.Zero
            };
        }

        public MemorySettings Settings { get; }
        public FakeTripPlanner Planner { get; }
        public FakeGeocoder Geocoder { get; }
        public TripPlannerViewModel Vm { get; }
    }

    [Fact]
    public async Task Find_trips_uses_the_address_result_the_user_just_searched_for_the_destination()
    {
        var f = new PlannerFixture();
        f.Vm.DestinationQuery = "Herzl";
        await f.Vm.SearchDestinationAddressCommand.ExecuteAsync(null);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        // Not the alphabetically first stop that also matches "Herzl".
        Assert.Equal(32.0600, f.Planner.LastRequest!.DestinationLatitude, 4);
    }

    [Fact]
    public async Task Find_trips_uses_the_searched_address_for_the_origin_even_without_any_local_match()
    {
        var f = new PlannerFixture();
        f.Vm.SetDestination(f.Vm.Choices.First(c => c.Place?.Name == "Home"));
        f.Vm.OriginQuery = "Zzyzx Road";
        await f.Vm.SearchOriginAddressCommand.ExecuteAsync(null);

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.NotNull(f.Planner.LastRequest);
        Assert.Equal(32.0600, f.Planner.LastRequest!.OriginLatitude, 4);
    }

    [Fact]
    public async Task Find_trips_ignores_address_results_once_the_text_has_changed()
    {
        var f = new PlannerFixture();
        f.Vm.DestinationQuery = "Herzl";
        await f.Vm.SearchDestinationAddressCommand.ExecuteAsync(null);
        f.Vm.DestinationQuery = "Herzl stop";

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal(HerzlStop.Latitude, f.Planner.LastRequest!.DestinationLatitude, 4);
    }

    [Fact]
    public async Task A_date_rolled_to_tomorrow_automatically_returns_to_today_when_the_time_is_moved_ahead()
    {
        var f = new PlannerFixture();
        f.Vm.SetDestination(f.Vm.Choices.First(c => c.Place?.Name == "Home"));
        f.Vm.SelectedModeIndex = 1;
        f.Vm.TargetTime = new TimeSpan(8, 0, 0);
        await f.Vm.PlanCommand.ExecuteAsync(null);
        Assert.Equal(new DateTime(2026, 10, 21), f.Vm.TargetDate);

        f.Vm.TargetTime = new TimeSpan(10, 0, 0);

        Assert.Equal(new DateTime(2026, 10, 20), f.Vm.TargetDate);
        Assert.False(f.Vm.HasDateNotice);
        await f.Vm.PlanCommand.ExecuteAsync(null);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T07:00:00+00:00"), f.Planner.LastRequest!.TargetTime);
    }

    [Fact]
    public async Task A_date_the_user_picked_is_never_rolled_back()
    {
        var f = new PlannerFixture();
        f.Vm.SetDestination(f.Vm.Choices.First(c => c.Place?.Name == "Home"));
        f.Vm.SelectedModeIndex = 1;
        f.Vm.TargetTime = new TimeSpan(8, 0, 0);
        await f.Vm.PlanCommand.ExecuteAsync(null);

        f.Vm.SelectTomorrowCommand.Execute(null);
        f.Vm.TargetTime = new TimeSpan(10, 0, 0);

        Assert.Equal(new DateTime(2026, 10, 21), f.Vm.TargetDate);
    }

    private sealed class SettingsFixture
    {
        public MemorySettings Settings { get; } = new();
        public FakeGeocoder Geocoder { get; } = new();
        public SettingsViewModel Vm { get; }

        public SettingsFixture()
        {
            Vm = new SettingsViewModel(
                Settings, new SavedPlaceCollection(Settings), new TemplateLocalizer(), [], new ShellAndSettingsTests.FakeSchedule(),
                new FakeHistory(), new RecordingNotifier(), new FakeCalendar(), Geocoder, new NoSecrets());
        }
    }

    private sealed class NoSecrets : ISecretProvider
    {
        public string Describe => "none";

        public Task<string?> GetAsync(string name, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task A_failed_save_removes_the_address_place_and_keeps_the_form()
    {
        var f = new SettingsFixture();
        var address = new GeocodedPlace("Dizengoff Street 50, Tel Aviv", 32.08, 34.78);
        f.Geocoder.Result = new GeocodeResult(GeocodeStatus.Ok, [address]);
        f.Vm.AddressQuery = "dizengoff";
        await f.Vm.SearchAddressCommand.ExecuteAsync(null);
        f.Vm.NewAddressPlaceName = "Gym";
        f.Settings.SaveSucceeds = false;

        await f.Vm.AddAddressPlaceCommand.ExecuteAsync(null);

        Assert.Empty(f.Vm.Places);
        Assert.Empty(f.Settings.Config.SavedPlaces);
        Assert.Equal("PlaceSaveFailed", f.Vm.AddressStatus);
        Assert.Equal("Gym", f.Vm.NewAddressPlaceName);
        Assert.Equal(address, f.Vm.SelectedAddress);

        f.Settings.SaveSucceeds = true;
        await f.Vm.AddAddressPlaceCommand.ExecuteAsync(null);
        Assert.Single(f.Vm.Places);
        Assert.Equal("PlaceSaved", f.Vm.AddressStatus);
    }

    // ---- calendar client id ----

    private sealed class DelayedSecrets(string id, string secret) : ISecretProvider
    {
        public string Describe => "delayed";

        public async Task<string?> GetAsync(string name, CancellationToken ct)
        {
            await Task.Yield();
            return name == SecretNames.GoogleClientId ? id : name == SecretNames.GoogleClientSecret ? secret : null;
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            var json = request.Method == HttpMethod.Post ? "{\"access_token\":\"tok\",\"expires_in\":3600}" : "{\"items\":[]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (GoogleCalendarService Service, RecordingHandler Handler, string Path) NewCalendar(ISecretProvider secrets)
    {
        var path = Path.Combine(Path.GetTempPath(), "buspulse-test-" + Guid.NewGuid().ToString("N") + ".dpapi");
        var store = new CalendarTokenStore(path);
        store.Save("refresh-token");
        var handler = new RecordingHandler();
        return (new GoogleCalendarService(new AppConfig(), secrets, new Factory(handler), store, NullLogger<GoogleCalendarService>.Instance), handler, path);
    }

    [Fact]
    public async Task Upcoming_events_resolve_the_client_id_before_the_token_request()
    {
        var (service, handler, path) = NewCalendar(new DelayedSecrets("my-id", "my-secret"));
        try
        {
            await service.GetUpcomingAsync(Now);

            Assert.Contains("client_id=my-id", handler.Bodies[0]);
            Assert.Contains("client_secret=my-secret", handler.Bodies[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Upcoming_events_are_empty_and_nothing_is_sent_when_no_client_id_exists()
    {
        var (service, handler, path) = NewCalendar(new NoSecrets());
        try
        {
            var events = await service.GetUpcomingAsync(Now);

            Assert.Empty(events);
            Assert.Empty(handler.Bodies);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
