using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
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

/// <summary>Regression tests for defects found in the code review.</summary>
public sealed class ReviewFixTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop HomeStop = new("100", "Home stop", "City", 32.1000, 34.8000, Id: 1);
    private static readonly Stop WorkStop = new("200", "Work stop", "City", 32.1100, 34.8000, Id: 2);

    [Fact]
    public async Task Plan_AnHttpTimeout_ShowsAFailureInsteadOfThrowing()
    {
        // HttpClient reports a timeout as TaskCanceledException even though nobody cancelled.
        var f = new PlannerFixture();
        f.Planner.Throw = new TaskCanceledException("timeout");

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal("PlanFailed", f.Vm.StatusText);
        Assert.False(f.Vm.IsBusy);
    }

    [Fact]
    public async Task Refresh_AnHttpTimeout_IsSwallowedSoTheTickerKeepsRunning()
    {
        var f = new PlannerFixture();
        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [Option(10, 5)]);
        await f.Vm.PlanCommand.ExecuteAsync(null);
        f.Planner.Throw = new TaskCanceledException("timeout");

        var refresh = Record.ExceptionAsync(() => f.Vm.RefreshAsync());

        Assert.Null(await refresh);
    }

    [Fact]
    public async Task Stops_AnHttpTimeoutWhileLoadingTheCatalog_ShowsTheRetryMessage()
    {
        var settings = new MemorySettings();
        var catalog = new FakeCatalog { Throw = new TaskCanceledException("timeout") };
        var vm = new StopsViewModel(
            settings, new FakeLocation(), catalog, new NullAggregator(), new SavedPlaceCollection(settings), new TemplateLocalizer(),
            new FixedClock(Now), new ImmediateDispatcher(), NullLogger<StopsViewModel>.Instance);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal("StopsLoadFailed", vm.StatusText);
    }

    [Fact]
    public void Planner_AddingASavedPlace_KeepsOriginAndDestinationSelected()
    {
        var f = new PlannerFixture();
        var work = f.Vm.Choices.First(choice => choice.DisplayName == "Work");
        f.Vm.SetDestination(work);

        f.Places.Items.Add(new SavedPlace { Name = "Gym", StopCode = "300" });

        Assert.NotNull(f.Vm.SelectedOrigin);
        Assert.True(f.Vm.SelectedOrigin.IsMyLocation);
        Assert.Equal("Work", f.Vm.SelectedDestination!.DisplayName);
        Assert.Contains(f.Vm.SelectedDestination, f.Vm.Choices);
    }

    [Fact]
    public void Planner_PlanFromAStop_UsesAChoiceThatIsInTheList()
    {
        var f = new PlannerFixture();

        f.Vm.SetOrigin(HomeStop);
        f.Places.Items.Add(new SavedPlace { Name = "Gym", StopCode = "300" });

        Assert.Same(HomeStop, f.Vm.SelectedOrigin.Stop);
        Assert.Contains(f.Vm.SelectedOrigin, f.Vm.Choices);
    }

    [Fact]
    public void Lines_AddingASavedPlace_KeepsAValidOrigin()
    {
        var settings = new MemorySettings();
        var places = new SavedPlaceCollection(settings);
        var vm = new LinesViewModel(
            settings, places, new TemplateLocalizer(), new FakeCatalog(), new FakeLocation(), new LinesViewModelTests.FakeSchedule(),
            new FixedClock(Now), NullLogger<LinesViewModel>.Instance);
        vm.SetOrigin(HomeStop);

        places.Items.Add(new SavedPlace { Name = "Gym", StopCode = "300" });

        Assert.NotNull(vm.SelectedOrigin);
        Assert.Same(HomeStop, vm.SelectedOrigin.Stop);
        Assert.Contains(vm.SelectedOrigin, vm.Origins);
    }

    [Fact]
    public async Task Reminder_AMissedAlertForTheOldRide_DoesNotClearANewlySelectedRide()
    {
        var presenter = new HookPresenter();
        var reminder = new DepartureReminderService(
            new FakeTripPlanner(), new RecordingNotifier(), presenter, new FixedClock(Now.AddMinutes(5)),
            new AppConfig { LeadMinutes = 5, PollingSeconds = 15 }, new TemplateLocalizer(), NullLogger<DepartureReminderService>.Instance);
        var oldRide = SmartAlertEngineTests.Option(-10) with { RideId = 1, BusDeparture = Now.AddMinutes(1) };
        var newRide = SmartAlertEngineTests.Option(30) with { RideId = 2 };
        var request = new TripRequest(32.1, 34.8, 32.11, 34.8);
        reminder.Watch(request, oldRide);
        // The user selects another ride while the "missed" alert for the old one is being shown.
        presenter.OnToast = message => reminder.Watch(request, newRide);

        var shown = await reminder.TickAsync();

        Assert.Equal(BusPulse.Services.Notifications.AlertKind.Missed, shown!.Kind);
        Assert.Equal(2, reminder.Watched!.RideId);
    }

    private sealed class HookPresenter : BusPulse.Services.Notifications.IAlertPresenter
    {
        public Action<BusPulse.Services.Notifications.AlertMessage>? OnToast { get; set; }

        public void ShowToast(BusPulse.Services.Notifications.AlertMessage message)
        {
            var hook = OnToast;
            OnToast = null;
            hook?.Invoke(message);
        }

        public void ShowFullScreen(BusPulse.Services.Notifications.AlertMessage message)
        {
        }

        public void DismissAll()
        {
        }
    }

    [Fact]
    public async Task Reminder_ASlowReplan_DoesNotHoldUpTheAlert()
    {
        var clock = new FixedClock(Now);
        var reminder = new DepartureReminderService(
            new HangingPlanner(), new RecordingNotifier(), new RecordingPresenter(), clock,
            new AppConfig { LeadMinutes = 5, PollingSeconds = 15 }, new TemplateLocalizer(), NullLogger<DepartureReminderService>.Instance)
        {
            RefreshWait = TimeSpan.FromMilliseconds(100)
        };
        reminder.Watch(new TripRequest(32.1, 34.8, 32.11, 34.8), SmartAlertEngineTests.Option(4));
        clock.UtcNow = Now.AddSeconds(20);

        var started = DateTimeOffset.UtcNow;
        var message = await reminder.TickAsync();

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.NotNull(message);
        Assert.NotNull(reminder.Watched);
    }

    [Fact]
    public async Task Reminder_ReplansTheWatchedRideSpecifically()
    {
        var planner = new FakeTripPlanner();
        var clock = new FixedClock(Now);
        var reminder = new DepartureReminderService(
            planner, new RecordingNotifier(), new RecordingPresenter(), clock,
            new AppConfig { LeadMinutes = 5, PollingSeconds = 15 }, new TemplateLocalizer(), NullLogger<DepartureReminderService>.Instance);
        var option = SmartAlertEngineTests.Option(20) with { RideId = 77 };
        planner.Plan = new TripPlan(TripPlanStatus.Ok, [option]);
        reminder.Watch(new TripRequest(32.1, 34.8, 32.11, 34.8), option);
        clock.UtcNow = Now.AddSeconds(20);

        await reminder.TickAsync();

        Assert.Equal(77, planner.LastRequest!.WatchRideId);
    }

    private sealed class HangingPlanner : ITripPlannerService
    {
        public async Task<TripPlan> PlanAsync(TripRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new TripPlan(TripPlanStatus.NoOptions, []);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "oops")]
    [InlineData(HttpStatusCode.OK, "[]")]
    public async Task StopCatalog_AfterMidnight_KeepsYesterdaysListWhenTodayCannotBeLoaded(HttpStatusCode status, string body)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bp-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new SwitchHandler(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stride-stops-sample.json")));
            var clock = new FixedClock(DateTimeOffset.Parse("2026-10-20T20:00:00+00:00"));
            var catalog = new StrideStopCatalog(new Factory(handler), clock, NullLogger<StrideStopCatalog>.Instance, directory);
            Assert.Equal(5, (await catalog.GetStopsAsync()).Count);

            clock.UtcNow = DateTimeOffset.Parse("2026-10-21T00:30:00+03:00");
            handler.Status = status;
            handler.Body = body;
            var callsBefore = handler.Calls;

            var stops = await catalog.GetStopsAsync();
            await catalog.WhenRefreshedAsync();
            var again = await catalog.GetStopsAsync();
            await catalog.WhenRefreshedAsync();

            Assert.Equal(5, stops.Count);
            Assert.Same(stops, again);
            // One background attempt; the failure is not retried on every call.
            Assert.Equal(callsBefore + 1, handler.Calls);
            Assert.False(File.Exists(Path.Combine(directory, "stops-2026-10-21.json")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StopCatalog_OnANewDay_ServesTheLatestCacheAtOnceAndRefreshesInTheBackground()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bp-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stride-stops-sample.json"));
            var clock = new FixedClock(DateTimeOffset.Parse("2026-10-20T12:00:00+00:00"));
            await new StrideStopCatalog(new Factory(new SwitchHandler(fixture)), clock, NullLogger<StrideStopCatalog>.Instance, directory).GetStopsAsync();

            // A fresh start two weeks later: today's file is missing, the network is slow but works.
            clock.UtcNow = DateTimeOffset.Parse("2026-11-03T12:00:00+00:00");
            var handler = new SwitchHandler(fixture);
            var catalog = new StrideStopCatalog(new Factory(handler), clock, NullLogger<StrideStopCatalog>.Instance, directory);

            var stops = await catalog.GetStopsAsync();
            await catalog.WhenRefreshedAsync();

            Assert.Equal(5, stops.Count);
            Assert.Equal(1, handler.Calls);
            Assert.True(File.Exists(Path.Combine(directory, "stops-2026-11-03.json")));
            Assert.False(File.Exists(Path.Combine(directory, "stops-2026-10-20.json")));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StopCatalog_WithNothingLoadedYet_StillReportsTheFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bp-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new SwitchHandler("oops") { Status = HttpStatusCode.InternalServerError };
            var catalog = new StrideStopCatalog(new Factory(handler), new FixedClock(Now), NullLogger<StrideStopCatalog>.Instance, directory);

            await Assert.ThrowsAsync<HttpRequestException>(() => catalog.GetStopsAsync());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class SwitchHandler(string body) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = body;
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Curlbus_ConcurrentRequestsForOneStop_MakeASingleHttpCall()
    {
        var handler = new SlowHandler(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "curlbus-stop-3490.json"), Encoding.UTF8));
        var clock = new FixedClock(DateTimeOffset.Parse("2026-10-20T19:26:00+03:00"));
        var provider = new CurlbusArrivalsProvider(new Factory(handler), clock, NullLogger<CurlbusArrivalsProvider>.Instance);

        var results = await Task.WhenAll(provider.GetArrivalsAsync("3490"), provider.GetArrivalsAsync("3490"), provider.GetArrivalsAsync("3490"));

        Assert.Equal(1, handler.Calls);
        Assert.All(results, result => Assert.Equal(ProviderStatus.Ok, result.Status));
    }

    [Fact]
    public async Task TripOptions_FollowALanguageChange()
    {
        var localizer = new LanguageAwareLocalizer();
        var f = new PlannerFixture(localizer);
        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [Option(10, 5) with { FromEndpoint = "A", ToEndpoint = "B" }]);
        await f.Vm.PlanCommand.ExecuteAsync(null);
        var row = f.Vm.Options[0];
        Assert.Equal("LiveBadge:en", row.BadgeText);
        Assert.Equal("A ► B", row.RouteText);

        f.Settings.Config.Language = "he";
        localizer.SetLanguage("he");

        Assert.Equal("LiveBadge:he", row.BadgeText);
        Assert.Equal("A ◄ B", row.RouteText);
    }

    private static TripOption Option(long rideId, int leaveInMinutes) => new(
        rideId, "4", "Dan", null, null, HomeStop, 100, TimeSpan.FromMinutes(1), WorkStop, 100, TimeSpan.FromMinutes(1),
        Now.AddMinutes(leaveInMinutes + 2), Now.AddMinutes(leaveInMinutes + 20), Now.AddMinutes(leaveInMinutes),
        Now.AddMinutes(leaveInMinutes + 21), IsLive: true, ArrivalSource.Curlbus, Now);

    private sealed class PlannerFixture
    {
        public PlannerFixture(ILocalizer? localizer = null)
        {
            Settings = new MemorySettings();
            Places = new SavedPlaceCollection(Settings);
            Places.Items.Add(new SavedPlace { Name = "Home", StopCode = "100" });
            Places.Items.Add(new SavedPlace { Name = "Work", StopCode = "200" });
            Places.SyncToConfig();
            Planner = new FakeTripPlanner();
            var location = new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.09, 34.79, 20) };
            Vm = new TripPlannerViewModel(
                Settings, Places, localizer ?? new TemplateLocalizer(), Planner, new FakeCatalog(HomeStop, WorkStop), location,
                new FakeHistory(), new FakeReminder(), new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(),
                NullLogger<TripPlannerViewModel>.Instance, new FakeCalendar());
            Vm.SetDestination(Vm.Choices.First(choice => choice.Place?.Name == "Work"));
        }

        public MemorySettings Settings { get; }
        public SavedPlaceCollection Places { get; }
        public FakeTripPlanner Planner { get; }
        public TripPlannerViewModel Vm { get; }
    }

    private sealed class NullAggregator : ILiveDataAggregator
    {
        public Task<AggregatedArrivals> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AggregatedArrivals([], new Dictionary<ArrivalSource, ProviderStatus>()));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SlowHandler(string body) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(50, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
