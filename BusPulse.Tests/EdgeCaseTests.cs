using System.IO;
using System.Net.Http;
using System.Text;
using BusPulse.Configuration;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using BusPulse.Services.Notifications;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>Boundary and malformed-input cases that the main suites do not cover directly.</summary>
public sealed class EdgeCaseTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");

    [Fact]
    public void Alert_AtExactlyTwoMinutes_IsTheFullScreenPopup()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());

        var message = engine.Evaluate(SmartAlertEngineTests.Option(2), null, Now, 5);

        Assert.Equal(AlertKind.Popup, message!.Kind);
    }

    [Fact]
    public void Alert_WithLeadZero_NeverShowsLeaveNowAmber()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());

        var message = engine.Evaluate(SmartAlertEngineTests.Option(8), null, Now, leadMinutes: 0);

        Assert.Equal(AlertKind.Soon, message!.Kind);
    }

    [Fact]
    public void Alert_WithNoWalkAndNoWait_DoesNotCrashOrShowZeroWalk()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());
        var option = SmartAlertEngineTests.Option(8, walkMinutes: 0, waitMinutes: 0);

        var message = engine.Evaluate(option, null, Now, 5);

        Assert.NotNull(message);
        Assert.Contains("min walk", message!.Message);
    }

    [Fact]
    public void Alert_ExactlyAtDeparture_IsMissedNotLate()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());
        var option = SmartAlertEngineTests.Option(-1) with { BusDeparture = Now };

        Assert.Equal(AlertKind.Missed, engine.Evaluate(option, null, Now, 5)!.Kind);
    }

    [Fact]
    public void Alert_ResetAllowsTheSameStageToFireAgain()
    {
        var engine = new SmartAlertEngine(new TemplateLocalizer());
        var option = SmartAlertEngineTests.Option(1);
        Assert.NotNull(engine.Evaluate(option, null, Now, 5));

        engine.Reset();

        Assert.NotNull(engine.Evaluate(option, null, Now.AddSeconds(1), 5));
    }

    [Fact]
    public void Geo_AntipodalPoints_DoNotProduceNaN()
    {
        var meters = Geo.DistanceMeters(32, 35, -32, -145);

        Assert.False(double.IsNaN(meters));
        Assert.InRange(meters, 19_000_000, 20_100_000);
    }

    [Fact]
    public void Geo_ZeroRadius_ReturnsNothingUnlessExactlyOnTop()
    {
        var stop = new Stop("1", "x", null, 32.1, 34.8, Id: 1);

        Assert.Empty(Geo.FindNearby([stop], 32.2, 34.8, count: 5, maxMeters: 0));
    }

    [Fact]
    public void Curlbus_VisitsWithWrongType_ReturnsNothingInsteadOfThrowing()
    {
        var provider = new CurlbusArrivalsProvider(
            new NullFactory(), new FixedClock(Now), NullLogger<CurlbusArrivalsProvider>.Instance);

        Assert.Empty(provider.Parse("3490", Encoding.UTF8.GetBytes("""{"errors":null,"visits":[1,2]}"""), Now));
        Assert.Empty(provider.Parse("3490", Encoding.UTF8.GetBytes("""{"errors":null,"visits":{"3490":"nope"}}"""), Now));
        Assert.Empty(provider.Parse("3490", Encoding.UTF8.GetBytes("""{"errors":null}"""), Now));
    }

    [Fact]
    public void Curlbus_VisitWithoutLineName_IsSkippedAndTheRestKept()
    {
        var provider = new CurlbusArrivalsProvider(
            new NullFactory(), new FixedClock(Now), NullLogger<CurlbusArrivalsProvider>.Instance);
        const string json = """{"errors":null,"visits":{"3490":[{"eta":"2026-10-20 10:05:00+00:00"},{"line_name":"4","eta":"2026-10-20 10:07:00+00:00"}]}}""";

        var arrival = Assert.Single(provider.Parse("3490", Encoding.UTF8.GetBytes(json), Now));

        Assert.Equal("4", arrival.LineName);
    }

    [Fact]
    public void Stride_EmptyAndWrongShapes_AreHandled()
    {
        Assert.Empty(StrideRideScheduleSource.Parse("[]"u8));
        Assert.Throws<System.Text.Json.JsonException>(() => StrideRideScheduleSource.Parse("{}"u8).ToList());
        Assert.Null(StrideScheduleProvider.ParseStopId("[]"u8));
    }

    [Fact]
    public void Stops_CodesWithWhitespaceOrHugeNumbers_AreRejectedByTheParser()
    {
        const string json = """[{"id":5,"code":99999999999999999999,"lat":32.1,"lon":34.8,"name":"big"}]""";

        Assert.Empty(StrideStopCatalog.ParseStops(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void History_NameExactlyAtTheLimit_IsKept_AndOneOverIsDropped()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bp-edge-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new HistoryService(new FixedClock(Now), NullLogger<HistoryService>.Instance, Path.Combine(dir, "h.json"));

            history.Record("a", new string('x', 64));
            history.Record("b", new string('x', 65));

            Assert.Single(history.Recent);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(14)]
    [InlineData(61)]
    public void Config_PollingOutOfRange_IsRejected(int seconds)
    {
        Assert.Throws<InvalidDataException>(() => new AppConfig { PollingSeconds = seconds }.Validate());
    }

    [Fact]
    public void Config_FavoritesWithBlankEntry_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new AppConfig { FavoriteStopCodes = ["  "] }.Validate());
    }

    [Fact]
    public void Config_HomePointingAtAMissingPlace_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => new AppConfig { HomePlaceId = "nope" }.Validate());
    }

    private sealed class NullFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
