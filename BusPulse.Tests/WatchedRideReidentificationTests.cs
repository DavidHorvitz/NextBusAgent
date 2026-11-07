using BusPulse.Configuration;
using BusPulse.Services.Notifications;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>A watched ride keeps being tracked when the planner switches feeds and the same trip gets another ride id.</summary>
public sealed class WatchedRideReidentificationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly TripRequest Request = new(32.1, 34.8, 32.11, 34.8, TripTimeMode.DepartAt, Now.AddHours(1));

    private static TripOption Ride(long id, int busInMinutes, string line = "4", string originCode = "100")
    {
        var bus = Now.AddMinutes(busInMinutes);
        return SmartAlertEngineTests.Option(0) with
        {
            RideId = id,
            Line = line,
            OriginStop = new Stop(originCode, "Origin stop", "City", 32.1, 34.8, Id: id),
            BusDeparture = bus,
            BusArrival = bus.AddMinutes(15),
            LeaveBy = bus.AddMinutes(-1),
            ArriveAt = bus.AddMinutes(15)
        };
    }

    [Fact]
    public async Task Replan_WatchedIdMissing_AdoptsSameLineAndOriginWithShiftedDeparture()
    {
        var f = new Fixture();
        f.Reminder.Watch(Request, Ride(1, 30));
        f.Planner.Plan = Plan(Ride(99, 33), Ride(100, 50));

        await f.TickAt(20);

        Assert.Equal(99, f.Reminder.Watched!.RideId);
        Assert.Equal(Now.AddMinutes(33), f.Reminder.Watched.BusDeparture);
    }

    [Fact]
    public async Task Replan_NothingMatches_KeepsTheLastKnownOption()
    {
        var f = new Fixture();
        var original = Ride(1, 30);
        f.Reminder.Watch(Request, original);
        f.Planner.Plan = Plan(Ride(99, 45), Ride(100, 60));

        await f.TickAt(20);

        Assert.Same(original, f.Reminder.Watched);
    }

    [Fact]
    public async Task Replan_OtherLineOrOtherOriginStop_IsNotAdopted()
    {
        var f = new Fixture();
        var original = Ride(1, 30);
        f.Reminder.Watch(Request, original);
        f.Planner.Plan = Plan(Ride(98, 30, line: "7"), Ride(99, 30, originCode: "555"));

        await f.TickAt(20);

        Assert.Same(original, f.Reminder.Watched);
    }

    [Fact]
    public async Task Replan_SeveralCandidates_AdoptsTheClosestDeparture()
    {
        var f = new Fixture();
        f.Reminder.Watch(Request, Ride(1, 30));
        f.Planner.Plan = Plan(Ride(97, 38), Ride(98, 28), Ride(99, 31));

        await f.TickAt(20);

        Assert.Equal(99, f.Reminder.Watched!.RideId);
    }

    [Fact]
    public async Task Replan_AfterAdoption_CarriesTheNewRideIdAndFindsItDirectly()
    {
        var f = new Fixture();
        f.Reminder.Watch(Request, Ride(1, 30));
        f.Planner.Plan = Plan(Ride(99, 33), Ride(100, 50));
        await f.TickAt(20);

        f.Planner.Plan = Plan(Ride(99, 34), Ride(100, 50));
        await f.TickAt(40);

        Assert.Equal(99, f.Planner.LastRequest!.WatchRideId);
        Assert.Equal(Now.AddMinutes(34), f.Reminder.Watched!.BusDeparture);
    }

    [Fact]
    public async Task Replan_AdoptedRide_IsNeverTheNextBus()
    {
        var f = new Fixture();
        f.Reminder.Watch(Request, Ride(1, 30));
        // The adopted trip is also the earliest upcoming option; another line follows later.
        f.Planner.Plan = Plan(Ride(99, 31), Ride(100, 45, line: "7"));

        await f.TickAt(20);
        // The next re-plan fails, so the alert uses the next bus chosen when the ride was adopted.
        f.Planner.Throw = new InvalidOperationException("offline");
        f.Clock.UtcNow = Now.AddMinutes(32);
        var message = await f.Reminder.TickAsync();

        Assert.Equal(AlertKind.Missed, message!.Kind);
        Assert.Contains("Line 7", message.Title);
    }

    [Fact]
    public async Task Missed_AfterAdoption_NamesADifferentNextBusThanTheWatchedOne()
    {
        var f = new Fixture();
        f.Reminder.Watch(Request, Ride(1, 30));
        f.Planner.Plan = Plan(Ride(99, 33), Ride(100, 50));
        await f.TickAt(20);

        // The bus is late (+34) and the clock passes it.
        f.Planner.Plan = Plan(Ride(99, 34), Ride(100, 50));
        f.Clock.UtcNow = Now.AddMinutes(35);
        var message = await f.Reminder.TickAsync();

        Assert.Equal(AlertKind.Missed, message!.Kind);
        Assert.Contains("in 15 min", message.Title);
    }

    private static TripPlan Plan(params TripOption[] options) => new(TripPlanStatus.Ok, options);

    private sealed class Fixture
    {
        public Fixture()
        {
            Clock = new FixedClock(Now);
            Planner = new FakeTripPlanner();
            Reminder = new DepartureReminderService(
                Planner, new RecordingNotifier(), new RecordingPresenter(), Clock,
                new AppConfig { LeadMinutes = 5, PollingSeconds = 15 }, new TemplateLocalizer(),
                NullLogger<DepartureReminderService>.Instance)
            {
                RefreshWait = TimeSpan.FromSeconds(5)
            };
        }

        public FixedClock Clock { get; }

        public FakeTripPlanner Planner { get; }

        public DepartureReminderService Reminder { get; }

        public async Task TickAt(int seconds)
        {
            Clock.UtcNow = Now.AddSeconds(seconds);
            await Reminder.TickAsync();
        }
    }
}
