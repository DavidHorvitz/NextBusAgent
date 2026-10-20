using System.IO;
using BusPulse.Configuration;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class DepartureReminderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop Origin = new("100", "Origin stop", "City", 32.1, 34.8, Id: 1);
    private static readonly Stop Destination = new("200", "Destination stop", "City", 32.11, 34.8, Id: 2);

    [Fact]
    public async Task TooEarly_DoesNotNotify()
    {
        var f = new Fixture();
        f.Watch(Option(leaveInMinutes: 30));

        var notified = await f.Reminder.CheckAsync();

        Assert.False(notified);
        Assert.Empty(f.Notifier.Sent);
    }

    [Fact]
    public async Task WithinLeadTime_NotifiesOnce_WithLineStopAndTimeOnly()
    {
        var f = new Fixture();
        f.Watch(Option(leaveInMinutes: 4));

        Assert.True(await f.Reminder.CheckAsync());
        Assert.False(await f.Reminder.CheckAsync());

        var sent = Assert.Single(f.Notifier.Sent);
        Assert.Equal(NotificationKind.Departure, sent.Kind);
        Assert.Contains("4", sent.Title);
        Assert.Contains("Origin stop", sent.Title);
        Assert.Contains("Leave in 4 min", sent.Message);
        Assert.DoesNotContain("Destination", sent.Title + sent.Message);
    }

    [Fact]
    public async Task LaterDelay_TriggersASingleDelayNotification()
    {
        var f = new Fixture();
        var option = Option(leaveInMinutes: 4);
        f.Watch(option);
        await f.Reminder.CheckAsync();

        f.Planner.Options = [option with { BusDeparture = option.BusDeparture.AddMinutes(4), LeaveBy = option.LeaveBy.AddMinutes(4), IsLive = true }];
        var delayed = await f.Reminder.CheckAsync();
        var again = await f.Reminder.CheckAsync();

        Assert.True(delayed);
        Assert.False(again);
        Assert.Equal([NotificationKind.Departure, NotificationKind.Delay], f.Notifier.Sent.Select(s => s.Kind).ToArray());
        Assert.Contains("Delayed by 4 min", f.Notifier.Sent[1].Message);
    }

    [Fact]
    public async Task SmallShift_IsNotReportedAsADelay()
    {
        var f = new Fixture();
        var option = Option(leaveInMinutes: 4);
        f.Watch(option);
        await f.Reminder.CheckAsync();

        f.Planner.Options = [option with { BusDeparture = option.BusDeparture.AddMinutes(1) }];
        await f.Reminder.CheckAsync();

        Assert.Single(f.Notifier.Sent);
    }

    [Fact]
    public async Task RideThatDisappearedLongAgo_StopsBeingWatched()
    {
        var f = new Fixture();
        f.Watch(Option(leaveInMinutes: -10));
        f.Planner.Options = [];

        await f.Reminder.CheckAsync();

        Assert.Null(f.Reminder.Watched);
    }

    [Fact]
    public async Task OptionWhoseLeaveTimeHasPassed_IsCancelled()
    {
        var f = new Fixture();
        var gone = Option(leaveInMinutes: -10);
        f.Watch(gone);
        f.Planner.Options = [gone];

        Assert.False(await f.Reminder.CheckAsync());

        Assert.Null(f.Reminder.Watched);
        Assert.Empty(f.Notifier.Sent);
    }

    [Fact]
    public async Task NothingWatched_ChecksNothing()
    {
        var f = new Fixture();

        Assert.False(await f.Reminder.CheckAsync());
        Assert.Equal(0, f.Planner.Calls);
    }

    [Fact]
    public void WatchAndCancel_RaiseChanged()
    {
        var f = new Fixture();
        var raised = 0;
        f.Reminder.Changed += (_, _) => raised++;

        f.Watch(Option(10));
        f.Reminder.Cancel();

        Assert.Equal(2, raised);
        Assert.Null(f.Reminder.Watched);
    }

    [Fact]
    public void ToastService_WhenMuted_DoesNothingAndDoesNotThrow()
    {
        var config = new AppConfig { NotificationsMuted = true };
        var service = new ToastNotificationService(config, new SoundService(config), NullLogger<ToastNotificationService>.Instance);

        service.Notify("4", "Leave now", NotificationKind.Departure);
    }

    [Fact]
    public void ToastService_BuildsXmlOnlyForNonEmptyText()
    {
        var config = new AppConfig();
        var service = new ToastNotificationService(config, new SoundService(config), NullLogger<ToastNotificationService>.Instance);

        Assert.NotNull(service.CreateToastXml("4", "Leave now"));
        Assert.Null(service.CreateToastXml("", "Leave now"));
    }

    private static TripOption Option(int leaveInMinutes) => new(
        RideId: 10,
        Line: "4",
        Operator: "Dan",
        FromEndpoint: null,
        ToEndpoint: null,
        OriginStop: Origin,
        WalkToStopMeters: 0,
        WalkToStop: TimeSpan.Zero,
        DestinationStop: Destination,
        WalkFromStopMeters: 0,
        WalkFromStop: TimeSpan.Zero,
        BusDeparture: Now.AddMinutes(leaveInMinutes + 1),
        BusArrival: Now.AddMinutes(leaveInMinutes + 20),
        LeaveBy: Now.AddMinutes(leaveInMinutes),
        ArriveAt: Now.AddMinutes(leaveInMinutes + 20),
        IsLive: false,
        Source: ArrivalSource.Schedule,
        ObservedAt: Now);

    private sealed class Fixture
    {
        public Fixture()
        {
            Planner = new FakePlanner();
            Notifier = new RecordingNotifier();
            Reminder = new DepartureReminderService(
                Planner, Notifier, new FixedClock(Now), new AppConfig { LeadMinutes = 5 }, NullLogger<DepartureReminderService>.Instance);
        }

        public FakePlanner Planner { get; }
        public RecordingNotifier Notifier { get; }
        public DepartureReminderService Reminder { get; }

        public void Watch(TripOption option)
        {
            Planner.Options = [option];
            Reminder.Watch(new TripRequest(32.1, 34.8, 32.11, 34.8), option);
        }
    }

    private sealed class FakePlanner : ITripPlannerService
    {
        public IReadOnlyList<TripOption> Options { get; set; } = [];
        public int Calls { get; private set; }

        public Task<TripPlan> PlanAsync(TripRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new TripPlan(Options.Count == 0 ? TripPlanStatus.NoOptions : TripPlanStatus.Ok, Options));
        }
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<(string Title, string Message, NotificationKind Kind)> Sent { get; } = [];

        public void Notify(string title, string message, NotificationKind kind) => Sent.Add((title, message, kind));
    }
}

public sealed class HistoryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "buspulse-history-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new(DateTimeOffset.Parse("2026-10-20T10:00:00+00:00"));

    private string FilePath => Path.Combine(_directory, "user_history.json");

    [Fact]
    public void Record_OrdersMostRecentFirst_AndDeduplicatesByPlace()
    {
        var history = Create();
        history.Record("a", "Home");
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        history.Record("b", "Work");
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        history.Record("a", "Home");

        Assert.Equal(["Home", "Work"], history.Recent.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void Record_IsCappedAtTheMaximum()
    {
        var history = Create();
        for (var i = 0; i < HistoryService.MaxEntries + 10; i++)
        {
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
            history.Record("p" + i, "Place " + i);
        }

        Assert.Equal(HistoryService.MaxEntries, history.Recent.Count);
        Assert.Equal("Place " + (HistoryService.MaxEntries + 9), history.Recent[0].Name);
    }

    [Fact]
    public void History_PersistsAcrossInstances_AndClearDeletesTheFile()
    {
        Create().Record("a", "Home");

        var reloaded = Create();
        Assert.Single(reloaded.Recent);
        Assert.True(File.Exists(FilePath));

        reloaded.Clear();

        Assert.Empty(reloaded.Recent);
        Assert.False(File.Exists(FilePath));
        Assert.Empty(Create().Recent);
    }

    [Fact]
    public void CorruptOrOversizedFiles_AreIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(Create().Recent);

        File.WriteAllText(FilePath, new string('x', 70 * 1024));
        Assert.Empty(Create().Recent);
    }

    [Theory]
    [InlineData("", "Home")]
    [InlineData("a", "")]
    public void Record_IgnoresEmptyValues(string id, string name)
    {
        var history = Create();

        history.Record(id, name);

        Assert.Empty(history.Recent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private HistoryService Create() => new(_clock, NullLogger<HistoryService>.Instance, FilePath);
}
