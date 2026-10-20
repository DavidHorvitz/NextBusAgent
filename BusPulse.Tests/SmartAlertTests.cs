using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using BusPulse.Services.Notifications;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class SmartAlertEngineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");
    private static readonly Stop Origin = new("100", "Origin stop", "City", 32.1, 34.8, Id: 1);
    private static readonly Stop Destination = new("200", "Destination stop", "City", 32.11, 34.8, Id: 2);

    [Theory]
    [InlineData(20, AlertKind.Upcoming, AlertUrgency.Normal)]
    [InlineData(8, AlertKind.Soon, AlertUrgency.Normal)]
    [InlineData(4, AlertKind.LeaveNow, AlertUrgency.Amber)]
    [InlineData(1, AlertKind.Popup, AlertUrgency.Red)]
    public void StageFollowsTheTimeToLeave(int leaveInMinutes, AlertKind kind, AlertUrgency urgency)
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());

        var message = engine.Evaluate(Option(leaveInMinutes), null, Now, leadMinutes: 5);

        Assert.NotNull(message);
        Assert.Equal(kind, message.Kind);
        Assert.Equal(urgency, message.Urgency);
        Assert.Equal(kind == AlertKind.Popup, message.FullScreen);
    }

    [Fact]
    public void LeadTime_MovesTheLeaveNowStage()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());

        Assert.Equal(AlertKind.LeaveNow, engine.Evaluate(Option(8), null, Now, leadMinutes: 10)!.Kind);

        engine.Reset();
        Assert.Equal(AlertKind.Soon, engine.Evaluate(Option(4), null, Now, leadMinutes: 0)!.Kind);
    }

    [Theory]
    [InlineData(20, 30)]
    [InlineData(8, 20)]
    [InlineData(4, 10)]
    public void EachStageRepeatsOnItsOwnInterval(int leaveInMinutes, int intervalSeconds)
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());
        var option = Option(leaveInMinutes);

        Assert.NotNull(engine.Evaluate(option, null, Now, 5));
        Assert.Null(engine.Evaluate(option, null, Now.AddSeconds(intervalSeconds - 1), 5));
        Assert.NotNull(engine.Evaluate(option, null, Now.AddSeconds(intervalSeconds), 5));
    }

    [Fact]
    public void MovingToANewStage_AlertsImmediately()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());
        var option = Option(leaveInMinutes: 11);
        Assert.Equal(AlertKind.Upcoming, engine.Evaluate(option, null, Now, 5)!.Kind);

        var message = engine.Evaluate(option, null, Now.AddMinutes(1).AddSeconds(5), 5);

        Assert.Equal(AlertKind.Soon, message!.Kind);
    }

    [Fact]
    public void FullScreenAlert_IsShownOnlyOnce()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());
        var option = Option(leaveInMinutes: 1);

        Assert.True(engine.Evaluate(option, null, Now, 5)!.FullScreen);
        Assert.Null(engine.Evaluate(option, null, Now.AddSeconds(30), 5));
    }

    [Fact]
    public void Messages_IncludeWalkWaitAndLeaveIn()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());

        // Leave in 8 minutes, 5 minutes walking and a 1 minute margin: the bus is 14 minutes away.
        var message = engine.Evaluate(Option(8, walkMinutes: 5, waitMinutes: 1), null, Now, 5)!;

        Assert.Equal("Line 4 departs in 14 min — you should leave soon", message.Title);
        Assert.Equal("5 min walk + 1 min wait = 6 min before departure · leave in 8 min", message.Message);
    }

    [Fact]
    public void TooLateToWalkThere_IsALateRedAlertInSeconds()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());
        // The bus leaves in 30 s but the stop is a 5 minute walk away.
        var option = Option(-5, walkMinutes: 5, waitMinutes: 0) with { BusDeparture = Now.AddSeconds(30) };

        var message = engine.Evaluate(option, null, Now, 5)!;

        Assert.Equal(AlertKind.Late, message.Kind);
        Assert.Equal(AlertUrgency.Red, message.Urgency);
        Assert.Equal("You are LATE — Line 4 departs in 30 seconds. HURRY!", message.Title);
    }

    [Fact]
    public void DepartedBus_IsReportedOnceWithTheNextOption()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());
        var missed = Option(-10) with { BusDeparture = Now.AddMinutes(-1) };
        var next = Option(5) with { RideId = 99, Line = "5", BusDeparture = Now.AddMinutes(8) };

        var message = engine.Evaluate(missed, next, Now, 5)!;

        Assert.Equal(AlertKind.Missed, message.Kind);
        Assert.Equal("You missed Line 4. Next option: Line 5 in 8 min", message.Title);
        Assert.Null(engine.Evaluate(missed, next, Now.AddMinutes(1), 5));
    }

    [Fact]
    public void DepartedBusWithoutAlternative_SaysSo()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());

        var message = engine.Evaluate(Option(-10) with { BusDeparture = Now.AddMinutes(-1) }, null, Now, 5)!;

        Assert.Equal("You missed Line 4.", message.Title);
    }

    internal static TripOption Option(int leaveInMinutes, int walkMinutes = 0, int waitMinutes = 1)
    {
        var leaveBy = Now.AddMinutes(leaveInMinutes);
        var bus = leaveBy.AddMinutes(walkMinutes + waitMinutes);
        return new TripOption(
            RideId: 10,
            Line: "4",
            Operator: "Dan",
            FromEndpoint: null,
            ToEndpoint: "Tel Aviv",
            OriginStop: Origin,
            WalkToStopMeters: walkMinutes * 75,
            WalkToStop: TimeSpan.FromMinutes(walkMinutes),
            DestinationStop: Destination,
            WalkFromStopMeters: 0,
            WalkFromStop: TimeSpan.Zero,
            BusDeparture: bus,
            BusArrival: bus.AddMinutes(15),
            LeaveBy: leaveBy,
            ArriveAt: bus.AddMinutes(15),
            IsLive: true,
            Source: ArrivalSource.Curlbus,
            ObservedAt: Now);
    }
}

public sealed class DepartureReminderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");

    [Fact]
    public void Watch_ShowsAlertAddedToastWithLineAndDestination_AndClearsOldAlerts()
    {
        var f = new Fixture();

        f.Watch(SmartAlertEngineTests.Option(30));

        var toast = Assert.Single(f.Notifier.Sent);
        Assert.Equal(NotificationKind.Info, toast.Kind);
        Assert.Equal("Alert added: Line 4 to Tel Aviv", toast.Title);
        Assert.Equal(1, f.Presenter.Dismissed);
    }

    [Fact]
    public async Task Tick_ShowsTheDueAlertAsAToast()
    {
        var f = new Fixture();
        f.Watch(SmartAlertEngineTests.Option(4));

        var message = await f.Reminder.TickAsync();

        Assert.Equal(AlertKind.LeaveNow, message!.Kind);
        Assert.Equal(message, Assert.Single(f.Presenter.Toasts));
        Assert.Empty(f.Presenter.FullScreen);
    }

    [Fact]
    public async Task Tick_UsesTheFullScreenAlertWhenItIsTimeToGo()
    {
        var f = new Fixture();
        f.Watch(SmartAlertEngineTests.Option(1));

        await f.Reminder.TickAsync();

        Assert.Single(f.Presenter.FullScreen);
        Assert.Empty(f.Presenter.Toasts);
    }

    [Fact]
    public async Task Tick_RePlansOnlyAfterThePollingInterval_AndPicksUpLiveChanges()
    {
        var f = new Fixture();
        var option = SmartAlertEngineTests.Option(20);
        f.Watch(option);

        await f.Reminder.TickAsync();
        Assert.Equal(0, f.Planner.Calls);

        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [option with { BusDeparture = option.BusDeparture.AddMinutes(3), LeaveBy = option.LeaveBy.AddMinutes(3) }]);
        f.Clock.UtcNow = Now.AddSeconds(20);
        await f.Reminder.TickAsync();

        Assert.Equal(1, f.Planner.Calls);
        Assert.Equal(option.BusDeparture.AddMinutes(3), f.Reminder.Watched!.BusDeparture);
    }

    [Fact]
    public async Task Missed_ShowsTheNextOptionAndStopsWatching()
    {
        var f = new Fixture();
        var option = SmartAlertEngineTests.Option(1);
        f.Watch(option);
        var next = option with { RideId = 99, Line = "5", BusDeparture = Now.AddMinutes(12), LeaveBy = Now.AddMinutes(10) };
        f.Planner.Plan = new TripPlan(TripPlanStatus.Ok, [next]);
        f.Clock.UtcNow = option.BusDeparture.AddSeconds(30);

        var message = await f.Reminder.TickAsync();

        Assert.Equal(AlertKind.Missed, message!.Kind);
        Assert.Contains("Next option: Line 5", message.Title);
        Assert.Null(f.Reminder.Watched);
    }

    [Fact]
    public async Task NothingSelected_ShowsNothing()
    {
        var f = new Fixture();

        Assert.Null(await f.Reminder.TickAsync());
        Assert.Equal(0, f.Planner.Calls);
        Assert.Empty(f.Presenter.Toasts);
    }

    [Fact]
    public void Cancel_DismissesAlertsAndRaisesChanged()
    {
        var f = new Fixture();
        var raised = 0;
        f.Reminder.Changed += (_, _) => raised++;
        f.Watch(SmartAlertEngineTests.Option(10));

        f.Reminder.Cancel();

        Assert.Null(f.Reminder.Watched);
        Assert.Equal(2, raised);
        Assert.Equal(2, f.Presenter.Dismissed);
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

    [Fact]
    public void LeadTime_AcceptsZeroToFifteenMinutes()
    {
        new AppConfig { LeadMinutes = 0 }.Validate();
        Assert.Throws<InvalidDataException>(() => new AppConfig { LeadMinutes = 16 }.Validate());
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            Clock = new FixedClock(Now);
            Planner = new FakeTripPlanner();
            Notifier = new RecordingNotifier();
            Presenter = new RecordingPresenter();
            Reminder = new DepartureReminderService(
                Planner, Notifier, Presenter, Clock, new AppConfig { LeadMinutes = 5, PollingSeconds = 20 }, new TemplateLocalizer(),
                NullLogger<DepartureReminderService>.Instance);
        }

        public FixedClock Clock { get; }
        public FakeTripPlanner Planner { get; }
        public RecordingNotifier Notifier { get; }
        public RecordingPresenter Presenter { get; }
        public DepartureReminderService Reminder { get; }

        public void Watch(TripOption option)
        {
            Planner.Plan = new TripPlan(TripPlanStatus.Ok, [option]);
            Reminder.Watch(new TripRequest(32.1, 34.8, 32.11, 34.8), option);
        }
    }
}

public sealed class LocalizationTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EnglishAndHebrew_DefineTheSameKeys()
    {
        var english = Load("en");
        var hebrew = Load("he");

        Assert.Empty(english.Keys.Except(hebrew.Keys));
        Assert.Empty(hebrew.Keys.Except(english.Keys));
    }

    [Fact]
    public void EveryTranslation_UsesTheSamePlaceholders()
    {
        var english = Load("en");
        var hebrew = Load("he");

        foreach (var (key, text) in english)
        {
            Assert.True(Placeholders(text).SetEquals(Placeholders(hebrew[key])), $"Placeholders differ for {key}");
        }
    }

    [Theory]
    [InlineData("AlertUpcoming")]
    [InlineData("AlertLeaveNow")]
    [InlineData("AlertLate")]
    [InlineData("AlertMissedNext")]
    [InlineData("PromptSelectRoute")]
    [InlineData("ToastAlertAdded")]
    public void HebrewAlerts_AreActuallyHebrew(string key)
    {
        Assert.Matches("[֐-׿]", Load("he")[key]);
    }

    [Fact]
    public void KeysUsedByTheAlertEngine_Exist()
    {
        var english = Load("en");

        foreach (var key in new[] { "AlertUpcoming", "AlertSoon", "AlertLeaveNow", "AlertPopupBody", "AlertPopupUnderMinute", "AlertLate", "AlertMissed", "AlertMissedNext", "AlertWalkDetail", "AlertAtStop", "PromptSelectRoute", "PromptEnableAlerts", "ToastAlertAdded", "ToastLeaveBy" })
        {
            Assert.True(english.ContainsKey(key), key);
        }
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).ToHashSet();

    private static Dictionary<string, string> Load(string language)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BusPulse.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, "BusPulse", "Resources", $"Strings.{language}.xaml");
        return XDocument.Load(path).Root!.Elements()
            .ToDictionary(element => (string)element.Attribute(X + "Key")!, element => element.Value);
    }
}

internal sealed class RecordingNotifier : INotifier
{
    public List<(string Title, string Message, NotificationKind Kind)> Sent { get; } = [];

    public void Notify(string title, string message, NotificationKind kind) => Sent.Add((title, message, kind));
}

internal sealed class RecordingPresenter : IAlertPresenter
{
    public List<AlertMessage> Toasts { get; } = [];
    public List<AlertMessage> FullScreen { get; } = [];
    public int Dismissed { get; private set; }

    public void ShowToast(AlertMessage message) => Toasts.Add(message);

    public void ShowFullScreen(AlertMessage message) => FullScreen.Add(message);

    public void DismissAll() => Dismissed++;
}
