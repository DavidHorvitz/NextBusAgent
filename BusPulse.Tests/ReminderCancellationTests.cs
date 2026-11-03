using BusPulse.Configuration;
using BusPulse.Services.Trips;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class ReminderCancellationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly TripRequest Request = new(32.1, 34.8, 32.11, 34.8);

    [Fact]
    public async Task Watch_AnotherRide_CancelsThePendingReplanAndReplansTheNewRideInTheSameTick()
    {
        var planner = new ControlledPlanner { HangingRideId = 1 };
        var clock = new FixedClock(Now);
        var reminder = Create(planner, clock);
        var rideA = SmartAlertEngineTests.Option(20) with { RideId = 1 };
        var rideB = SmartAlertEngineTests.Option(25) with { RideId = 2 };

        reminder.Watch(Request, rideA);
        clock.UtcNow = Now.AddSeconds(20);
        await reminder.TickAsync();
        Assert.Equal(1, planner.LastRequest!.WatchRideId);

        planner.HangingRideId = null;
        reminder.Watch(Request, rideB);
        clock.UtcNow = Now.AddSeconds(40);
        await reminder.TickAsync();

        Assert.True(planner.TokenOfHangingCall.IsCancellationRequested);
        Assert.Equal(2, planner.LastRequest!.WatchRideId);
        Assert.Equal(2, planner.Calls);
    }

    [Fact]
    public async Task Cancel_WhilePlanning_CancelsThePendingReplan()
    {
        var planner = new ControlledPlanner { HangingRideId = 1 };
        var clock = new FixedClock(Now);
        var reminder = Create(planner, clock);
        reminder.Watch(Request, SmartAlertEngineTests.Option(20) with { RideId = 1 });
        clock.UtcNow = Now.AddSeconds(20);
        await reminder.TickAsync();
        Assert.False(planner.TokenOfHangingCall.IsCancellationRequested);

        reminder.Cancel();

        Assert.True(planner.TokenOfHangingCall.IsCancellationRequested);
        Assert.Null(reminder.Watched);
    }

    private static DepartureReminderService Create(ITripPlannerService planner, FixedClock clock) =>
        new(planner, new RecordingNotifier(), new RecordingPresenter(), clock,
            new AppConfig { LeadMinutes = 5, PollingSeconds = 15 }, new TemplateLocalizer(), NullLogger<DepartureReminderService>.Instance)
        {
            RefreshWait = TimeSpan.FromMilliseconds(50)
        };

    private sealed class ControlledPlanner : ITripPlannerService
    {
        public long? HangingRideId { get; set; }

        public TripRequest? LastRequest { get; private set; }

        public int Calls { get; private set; }

        public CancellationToken TokenOfHangingCall { get; private set; }

        public async Task<TripPlan> PlanAsync(TripRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastRequest = request;
            if (request.WatchRideId is { } id && id == HangingRideId)
            {
                TokenOfHangingCall = cancellationToken;
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return new TripPlan(TripPlanStatus.NoOptions, []);
        }
    }
}
