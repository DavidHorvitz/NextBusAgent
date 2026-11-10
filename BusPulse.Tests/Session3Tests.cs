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
using BusPulse.Views.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class WalkingRouterTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-10T10:00:00+02:00");

    // Two points in Tel Aviv about 340 m apart (made-up trip, not the owner's location).
    private const double LatA = 32.0853, LonA = 34.7818, LatB = 32.0870, LonB = 34.7790;

    private static byte[] Answer(double meters) =>
        Encoding.UTF8.GetBytes($$"""{"code":"Ok","routes":[{"distance":{{meters.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"duration":300.0,"legs":[]}],"waypoints":[]}""");

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static (OsrmWalkingRouter Router, StubHandler Handler, MutableClock Clock, List<TimeSpan> Delays) Create(StubHandler handler)
    {
        var clock = new MutableClock(Start);
        var delays = new List<TimeSpan>();
        var router = new OsrmWalkingRouter(new StubFactory(handler), clock, NullLogger<OsrmWalkingRouter>.Instance, (wait, _) =>
        {
            delays.Add(wait);
            return Task.CompletedTask;
        });
        return (router, handler, clock, delays);
    }

    [Fact]
    public async Task RoutedWalk_UsesTheServiceDistance_AndIsNotAnEstimate()
    {
        var (router, handler, _, _) = Create(StubHandler.Ok(Answer(410)));

        var walk = await router.GetWalkAsync(LatA, LonA, LatB, LonB);

        Assert.False(walk.IsEstimate);
        Assert.Equal(410, walk.Meters);
        Assert.Equal(Walking.TimeFor(410), walk.Duration);
        Assert.Equal(1, handler.Calls);
        Assert.Contains("/routed-foot/route/v1/foot/34.781800,32.085300;34.779000,32.087000", handler.LastUri!.ToString());
    }

    [Fact]
    public async Task SameWalkTwice_IsServedFromTheCache()
    {
        var (router, handler, _, _) = Create(StubHandler.Ok(Answer(410)));

        await router.GetWalkAsync(LatA, LonA, LatB, LonB);
        await router.GetWalkAsync(LatA, LonA, LatB, LonB);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ServiceFailure_FallsBackToStraightLineTimes1Point3_Labelled()
    {
        var (router, _, _, _) = Create(StubHandler.Status(HttpStatusCode.InternalServerError));
        var straight = Geo.DistanceMeters(LatA, LonA, LatB, LonB);

        var walk = await router.GetWalkAsync(LatA, LonA, LatB, LonB);

        Assert.True(walk.IsEstimate);
        Assert.Equal(straight * 1.3, walk.Meters, 3);
    }

    [Fact]
    public async Task ImplausibleDistance_FallsBackToTheEstimate()
    {
        var (router, _, _, _) = Create(StubHandler.Ok(Answer(90_000)));

        Assert.True((await router.GetWalkAsync(LatA, LonA, LatB, LonB)).IsEstimate);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task After429Or403_NoCallIsMadeForTenMinutes(HttpStatusCode status)
    {
        var (router, handler, clock, _) = Create(StubHandler.Status(status));

        Assert.True((await router.GetWalkAsync(LatA, LonA, LatB, LonB)).IsEstimate);
        Assert.True(router.IsPaused);
        clock.UtcNow = Start.AddMinutes(9);
        Assert.True((await router.GetWalkAsync(LatA + 0.001, LonA, LatB, LonB)).IsEstimate);
        Assert.Equal(1, handler.Calls);

        clock.UtcNow = Start.AddMinutes(11);
        await router.GetWalkAsync(LatA + 0.002, LonA, LatB, LonB);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Requests_AreSpacedAtLeastOneSecond_AndCarryAnIdentifyingUserAgent()
    {
        string? agent = null;
        var handler = new StubHandler(request =>
        {
            agent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Answer(410)) });
        });
        var (router, _, _, delays) = Create(handler);

        await router.GetWalkAsync(LatA, LonA, LatB, LonB);
        await router.GetWalkAsync(LatA + 0.001, LonA, LatB, LonB);

        Assert.Contains("BusPulse", agent);
        Assert.Contains(delays, wait => wait >= TimeSpan.FromMilliseconds(900));
    }

    [Fact]
    public async Task VeryShortWalk_NeedsNoCall()
    {
        var (router, handler, _, _) = Create(StubHandler.Status(HttpStatusCode.InternalServerError));

        var walk = await router.GetWalkAsync(LatA, LonA, LatA + 0.00005, LonA);

        Assert.False(walk.IsEstimate);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task PointsOutsideIsrael_AreNotSent()
    {
        var (router, handler, _, _) = Create(StubHandler.Ok(Answer(410)));

        Assert.True((await router.GetWalkAsync(48.85, 2.35, 48.86, 2.36)).IsEstimate);
        Assert.Equal(0, handler.Calls);
    }
}

public sealed class TripWalkingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop StopA = new("100", "Origin stop", "Bnei Brak", 32.1000, 34.8000, Id: 1);
    private static readonly Stop StopB = new("200", "Destination stop", "Tel Aviv", 32.1100, 34.8000, Id: 2);

    private sealed class FakeRouter(Func<double, double, double, double, WalkRoute> answer) : IWalkingRouter
    {
        public int Calls { get; private set; }

        public Task<WalkRoute> GetWalkAsync(double a, double b, double c, double d, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(answer(a, b, c, d));
        }
    }

    private static TripPlannerService Planner(TripPlannerServiceTests.FakeRideSchedule schedule, IWalkingRouter? router, params Stop[] stops) =>
        new(new FakeCatalog(stops), schedule, new TripPlannerServiceTests.FakeAggregator(), new FixedClock(Now), NullLogger<TripPlannerService>.Instance, null, null, router);

    private static RideStop Departure(long ride, int minutes) =>
        new(ride, 1, 2, Now.AddMinutes(minutes), "4", "Dan", "Origin-City<->Destination-City-1");

    private static RideStop Row(long ride, int minutes) => new(ride, 2, 5, Now.AddMinutes(minutes), string.Empty, null, null);

    [Fact]
    public async Task RoutedWalks_SetLeaveByArrivalAndMeters_AndAreNotEstimates()
    {
        var schedule = new TripPlannerServiceTests.FakeRideSchedule();
        schedule.Departures[1] = [Departure(10, 20)];
        schedule.DestinationRows = [Row(10, 40)];
        // Walk to the stop 5 minutes (360 m), walk from the final stop 3 minutes (150 m).
        var router = new FakeRouter((fromLat, _, _, _) =>
            Math.Abs(fromLat - 32.1000) < 0.0001 ? new WalkRoute(360, TimeSpan.FromMinutes(5), false) : new WalkRoute(150, TimeSpan.FromMinutes(3), false));
        var planner = Planner(schedule, router, StopA, StopB);

        var option = Assert.Single((await planner.PlanAsync(new TripRequest(32.1000, 34.8000, 32.1100, 34.8000))).Options);

        Assert.Equal(TimeSpan.FromMinutes(5), option.WalkToStop);
        Assert.Equal(360, option.WalkToStopMeters);
        Assert.False(option.WalkToIsEstimate);
        Assert.False(option.WalkFromIsEstimate);
        Assert.Equal(Now.AddMinutes(20) - TimeSpan.FromMinutes(5) - TimeSpan.FromMinutes(1), option.LeaveBy);
        Assert.Equal(Now.AddMinutes(40) + TimeSpan.FromMinutes(3), option.ArriveAt);
    }

    [Fact]
    public async Task WithoutARouter_WalksAreEstimates()
    {
        var schedule = new TripPlannerServiceTests.FakeRideSchedule();
        schedule.Departures[1] = [Departure(10, 20)];
        schedule.DestinationRows = [Row(10, 40)];
        var planner = Planner(schedule, router: null, StopA, StopB);

        var option = Assert.Single((await planner.PlanAsync(new TripRequest(32.1000, 34.8000, 32.1100, 34.8000))).Options);

        Assert.True(option.WalkToIsEstimate);
        Assert.True(option.WalkFromIsEstimate);
    }

    [Fact]
    public async Task ARoutedWalkThatMakesTheBusUncatchable_DropsTheOption()
    {
        var schedule = new TripPlannerServiceTests.FakeRideSchedule();
        schedule.Departures[1] = [Departure(10, 4)];
        schedule.DestinationRows = [Row(10, 30)];
        var router = new FakeRouter((_, _, _, _) => new WalkRoute(900, TimeSpan.FromMinutes(12), false));
        var planner = Planner(schedule, router, StopA, StopB);

        var plan = await planner.PlanAsync(new TripRequest(32.1000, 34.8000, 32.1100, 34.8000));

        Assert.Empty(plan.Options);
    }

    [Fact]
    public async Task AtMostFiveDirectOptions_AreReturned()
    {
        var schedule = new TripPlannerServiceTests.FakeRideSchedule();
        schedule.Departures[1] = Enumerable.Range(1, 9).Select(i => Departure(i, 5 + i)).ToArray();
        schedule.DestinationRows = Enumerable.Range(1, 9).Select(i => Row(i, 20 + i)).ToArray();
        var planner = Planner(schedule, new FakeRouter((_, _, _, _) => new WalkRoute(0, TimeSpan.Zero, false)), StopA, StopB);

        var plan = await planner.PlanAsync(new TripRequest(32.1000, 34.8000, 32.1100, 34.8000));

        Assert.Equal(5, plan.Options.Count);
    }

    [Fact]
    public async Task OneRouteCallPerDistinctStop_NotPerOption()
    {
        var schedule = new TripPlannerServiceTests.FakeRideSchedule();
        schedule.Departures[1] = Enumerable.Range(1, 4).Select(i => Departure(i, 5 + i)).ToArray();
        schedule.DestinationRows = Enumerable.Range(1, 4).Select(i => Row(i, 20 + i)).ToArray();
        var router = new FakeRouter((_, _, _, _) => new WalkRoute(0, TimeSpan.Zero, false));
        var planner = Planner(schedule, router, StopA, StopB);

        await planner.PlanAsync(new TripRequest(32.1000, 34.8000, 32.1100, 34.8000));

        Assert.Equal(2, router.Calls);
    }
}

public sealed class TripResultsViewModelTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-10T10:00:00+00:00");
    private static readonly Stop Origin = new("100", "Origin stop", "Bnei Brak", 32.10, 34.80, Id: 1);
    private static readonly Stop Target = new("200", "Destination stop", "Tel Aviv", 32.11, 34.80, Id: 2);

    private sealed class MutableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            var settings = new MemorySettings();
            Places = new SavedPlaceCollection(settings);
            Planner = new FakeTripPlanner();
            Clock = new MutableClock(Start);
            Vm = new TripPlannerViewModel(
                settings, Places, new TemplateLocalizer(), Planner, new FakeCatalog(Origin, Target),
                new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.10, 34.80, 20) }, new FakeHistory(), new FakeReminder(),
                new RecordingNotifier(), Clock, new ImmediateDispatcher(), NullLogger<TripPlannerViewModel>.Instance, new FakeCalendar())
            {
                DebounceDelay = TimeSpan.Zero
            };
            Vm.DestinationQuery = "Destination stop";
        }

        public SavedPlaceCollection Places { get; }
        public FakeTripPlanner Planner { get; }
        public MutableClock Clock { get; }
        public TripPlannerViewModel Vm { get; }

        public Task PlanAsync() => Vm.PlanCommand.ExecuteAsync(null);
    }

    private static TripOption Option(long ride, string line, int busInMinutes, bool live = false, string? color = null) =>
        new(ride, line, "Dan", "A", "B", Origin, 300, TimeSpan.FromMinutes(4), Target, 150, TimeSpan.FromMinutes(2),
            Start.AddMinutes(busInMinutes), Start.AddMinutes(busInMinutes + 20), Start.AddMinutes(busInMinutes - 5), Start.AddMinutes(busInMinutes + 22),
            live, live ? ArrivalSource.Curlbus : ArrivalSource.Schedule, Start, color, WalkToIsEstimate: true, WalkFromIsEstimate: false);

    private static TripPlan Plan(params TripOption[] options) => new(TripPlanStatus.Ok, options);

    [Fact]
    public async Task Row_ShowsLeaveByWalkLegsEstimateLabelAndHeroMinutes()
    {
        var f = new Fixture();
        f.Planner.Plan = Plan(Option(1, "4", busInMinutes: 12, color: "FF9933"));

        await f.PlanAsync();

        var row = Assert.Single(f.Vm.Options);
        Assert.Equal("Origin stop", row.BoardingStop);
        Assert.Equal("12", row.EtaValue);
        Assert.Equal("FF9933", row.RouteColor);
        Assert.Equal("LeaveByAt", row.LeaveByText);
        // 4 minutes (300 m) is an estimate; 2 minutes (150 m) is routed. The template keys are the test localizer's fallback.
        Assert.StartsWith("WalkLeg", row.WalkToLegText);
        Assert.EndsWith("WalkEstimated", row.WalkToLegText);
        Assert.DoesNotContain("WalkEstimated", row.WalkFromLegText);
        Assert.Equal(DepartureStatus.Scheduled, row.Status);
    }

    [Fact]
    public async Task RefreshWithTheSameRidesInAnotherOrder_KeepsTheRowObjects()
    {
        var f = new Fixture();
        f.Planner.Plan = Plan(Option(1, "4", 10), Option(2, "18", 15), Option(3, "66", 20));
        await f.PlanAsync();
        var rows = f.Vm.Options.ToArray();
        var scrolled = new List<string>();
        f.Vm.Options.CollectionChanged += (_, e) => scrolled.Add(e.Action.ToString());

        f.Planner.Plan = Plan(Option(3, "66", 9), Option(1, "4", 10), Option(2, "18", 15));
        await f.Vm.RefreshAsync();

        Assert.Equal(new[] { rows[2], rows[0], rows[1] }, f.Vm.Options.ToArray());
        Assert.DoesNotContain("Reset", scrolled);
        Assert.DoesNotContain("Add", scrolled);
        Assert.Equal("9", rows[2].EtaValue);
    }

    [Fact]
    public async Task RefreshWithOneRideLess_RemovesOnlyThatRow()
    {
        var f = new Fixture();
        f.Planner.Plan = Plan(Option(1, "4", 10), Option(2, "18", 15), Option(3, "66", 20));
        await f.PlanAsync();
        var rows = f.Vm.Options.ToArray();

        f.Planner.Plan = Plan(Option(1, "4", 10), Option(3, "66", 20));
        await f.Vm.RefreshAsync();

        Assert.Equal(new[] { rows[0], rows[2] }, f.Vm.Options.ToArray());
    }

    [Fact]
    public async Task UpdatedAgo_CountsUp_AndWarnsAfterNinetySeconds()
    {
        var f = new Fixture();
        f.Planner.Plan = Plan(Option(1, "4", 10));
        await f.PlanAsync();
        Assert.Equal("0s ago", f.Vm.UpdatedAgoText);
        Assert.False(f.Vm.IsUpdateStale);

        f.Clock.UtcNow = Start.AddSeconds(45);
        f.Vm.Tick();
        Assert.Equal("45s ago", f.Vm.UpdatedAgoText);
        Assert.False(f.Vm.IsUpdateStale);

        f.Clock.UtcNow = Start.AddSeconds(100);
        f.Vm.Tick();
        Assert.True(f.Vm.IsUpdateStale);

        // A failed refresh does not reset the age.
        f.Planner.Throw = new HttpRequestException("down");
        await f.Vm.RefreshAsync();
        Assert.True(f.Vm.IsUpdateStale);

        f.Planner.Throw = null;
        await f.Vm.RefreshAsync();
        Assert.False(f.Vm.IsUpdateStale);
        Assert.Equal("0s ago", f.Vm.UpdatedAgoText);
    }

    [Fact]
    public void RefreshInterval_IsThirtySeconds() => Assert.Equal(TimeSpan.FromSeconds(30), TripPlannerViewModel.RefreshInterval);

    [Fact]
    public async Task RefreshNowCommand_PlansAgainAtOnce()
    {
        var f = new Fixture();
        f.Planner.Plan = Plan(Option(1, "4", 10));
        await f.PlanAsync();
        var calls = f.Planner.Calls;

        await f.Vm.RefreshNowCommand.ExecuteAsync(null);

        Assert.Equal(calls + 1, f.Planner.Calls);
    }

    [Fact]
    public async Task LineFilterWithNothingFound_SaysThereIsNoDataForThatLine()
    {
        var f = new Fixture();
        f.Vm.LineName = "999";
        f.Planner.Plan = new TripPlan(TripPlanStatus.NoOptions, []);

        await f.PlanAsync();

        Assert.Equal("NoDataForLine", f.Vm.StatusText);
        Assert.Equal("999", f.Planner.LastRequest!.PreferredLine);
    }

    [Fact]
    public async Task LineSuggestions_ComeFromLinesAlreadySeen_AndFilterByWhatIsTyped()
    {
        var f = new Fixture();
        Assert.Empty(f.Vm.LineSuggestions);
        f.Planner.Plan = Plan(Option(1, "4", 10), Option(2, "47", 15), Option(3, "66", 20));
        await f.PlanAsync();

        f.Vm.LineName = "4";

        Assert.Equal(["47"], f.Vm.LineSuggestions.ToArray());
        f.Vm.LineName = string.Empty;
        Assert.Equal(new[] { "4", "47", "66" }, f.Vm.LineSuggestions.ToArray());
        f.Vm.UseLineCommand.Execute("66");
        Assert.Equal("66", f.Vm.LineName);
    }

    [Fact]
    public async Task PlanningProgress_AppearsOnlyAfterTheDelay()
    {
        var f = new Fixture();
        f.Vm.SpinnerDelay = TimeSpan.FromHours(1);
        bool? duringPlan = null;
        f.Planner.Plan = Plan(Option(1, "4", 10));
        f.Planner.OnPlan = () => duringPlan = f.Vm.ShowProgress;

        await f.PlanAsync();

        Assert.False(duringPlan);
        Assert.False(f.Vm.ShowProgress);
        Assert.False(f.Vm.IsBusy);
    }

    [Fact]
    public async Task PlanningProgress_WithNoDelay_IsShownForTheRequestOnly()
    {
        var f = new Fixture();
        f.Vm.SpinnerDelay = TimeSpan.Zero;
        bool? duringPlan = null;
        f.Planner.Plan = Plan(Option(1, "4", 10));
        f.Planner.OnPlan = () => duringPlan = f.Vm.ShowProgress;

        await f.PlanAsync();

        Assert.True(duringPlan);
        Assert.False(f.Vm.ShowProgress);
    }

    [Fact]
    public void QuickChip_FromNow_SwitchesToDepartAtAndSetsNowPlusMinutes()
    {
        var f = new Fixture();
        Assert.Equal(TripTimeMode.Now, f.Vm.Mode);
        // 10:00 UTC is 12:00 in Israel on 2026-11-10 (winter time, UTC+2).

        f.Vm.AddMinutesCommand.Execute("30");

        Assert.Equal(TripTimeMode.DepartAt, f.Vm.Mode);
        Assert.Equal(new TimeSpan(12, 30, 0), f.Vm.TargetTime);
        Assert.True(f.Vm.IsTodaySelected);
    }

    [Fact]
    public void QuickChip_PastMidnight_MovesToTomorrow()
    {
        var f = new Fixture();
        f.Clock.UtcNow = DateTimeOffset.Parse("2026-11-10T21:45:00+00:00"); // 23:45 in Israel

        f.Vm.AddMinutesCommand.Execute("30");

        Assert.Equal(new TimeSpan(0, 15, 0), f.Vm.TargetTime);
        Assert.True(f.Vm.IsTomorrowSelected);
        Assert.False(f.Vm.IsTodaySelected);
    }

    [Fact]
    public void DateChips_FollowTheChosenDay_AndTheThirdChipShowsALaterDate()
    {
        var f = new Fixture();
        f.Vm.SelectedModeIndex = 1;
        Assert.True(f.Vm.IsTodaySelected);

        f.Vm.IsTomorrowSelected = true;
        Assert.True(f.Vm.IsTomorrowSelected);
        Assert.False(f.Vm.IsOtherDateSelected);

        f.Vm.TargetDate = new DateTime(2026, 11, 20);
        Assert.True(f.Vm.IsOtherDateSelected);
        Assert.False(f.Vm.IsTomorrowSelected);
        Assert.NotEqual("DatePick", f.Vm.DatePickText);

        // Clicking the chip that is on must not turn it off.
        f.Vm.IsTomorrowSelected = false;
        f.Vm.IsTodaySelected = true;
        f.Vm.IsTodaySelected = false;
        Assert.True(f.Vm.IsTodaySelected);
    }
}

public sealed class TimeFieldPickerTests
{
    [Fact]
    public void ButtonsStepTheMinuteByFive_WithCarry_AndWrapAtMidnight() =>
        Wpf.Run(() =>
        {
            var picker = new TimeFieldPicker { Value = new TimeSpan(10, 58, 0) };

            picker.Step(TimeSpan.FromMinutes(TimeFieldPicker.ButtonMinuteStep));
            Assert.Equal(new TimeSpan(11, 3, 0), picker.Value);

            picker.Value = new TimeSpan(23, 58, 0);
            picker.Step(TimeSpan.FromMinutes(5));
            Assert.Equal(new TimeSpan(0, 3, 0), picker.Value);

            picker.Step(TimeSpan.FromMinutes(-5));
            Assert.Equal(new TimeSpan(23, 58, 0), picker.Value);
        });

    [Fact]
    public void TypedDigits_AreClampedToAValidHourAndMinute() =>
        Wpf.Run(() =>
        {
            var picker = new TimeFieldPicker { Value = new TimeSpan(9, 30, 0) };

            picker.HourBox.Text = "27";
            picker.Commit(picker.HourBox);
            Assert.Equal(new TimeSpan(23, 30, 0), picker.Value);

            picker.MinuteBox.Text = "75";
            picker.Commit(picker.MinuteBox);
            Assert.Equal(new TimeSpan(23, 59, 0), picker.Value);

            picker.HourBox.Text = "7";
            picker.Commit(picker.HourBox);
            Assert.Equal(new TimeSpan(7, 59, 0), picker.Value);
            Assert.Equal("07", picker.HourBox.Text);
        });

    [Fact]
    public void AnEmptyOrInvalidField_RestoresTheCurrentValue() =>
        Wpf.Run(() =>
        {
            var picker = new TimeFieldPicker { Value = new TimeSpan(9, 5, 0) };

            picker.MinuteBox.Text = string.Empty;
            picker.Commit(picker.MinuteBox);

            Assert.Equal(new TimeSpan(9, 5, 0), picker.Value);
            Assert.Equal("05", picker.MinuteBox.Text);
        });

    [Fact]
    public void ValueChanges_AreShownInBothFields() =>
        Wpf.Run(() =>
        {
            var picker = new TimeFieldPicker();

            picker.Value = new TimeSpan(14, 7, 0);

            Assert.Equal("14", picker.HourBox.Text);
            Assert.Equal("07", picker.MinuteBox.Text);
        });
}
