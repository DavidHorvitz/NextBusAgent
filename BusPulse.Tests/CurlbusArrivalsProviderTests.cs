using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class CurlbusArrivalsProviderTests
{
    private const string StopCode = "3490";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T19:26:00+03:00");

    [Fact]
    public void Parse_RealFixture_ReturnsLiveArrivalsOrderedByEta()
    {
        var provider = CreateProvider(new FakeHandler(HttpStatusCode.OK, Fixture()), new FakeClock(Now));

        var arrivals = provider.Parse(StopCode, Encoding.UTF8.GetBytes(Fixture()), Now);

        Assert.Equal(3, arrivals.Count);
        Assert.All(arrivals, arrival =>
        {
            Assert.True(arrival.IsLive);
            Assert.Equal(ArrivalSource.Curlbus, arrival.Source);
            Assert.Equal(StopCode, arrival.StopCode);
        });
        Assert.Equal(arrivals.OrderBy(a => a.Eta).ToArray(), arrivals);
        Assert.Equal("4", arrivals[0].LineName);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T19:36:00+03:00"), arrivals[0].Eta);
        Assert.NotNull(arrivals[0].Destination);
    }

    [Theory]
    [InlineData("2036-10-20 19:36:00+03:00")]
    [InlineData("2026-10-20 18:00:00+03:00")]
    [InlineData("2026-10-21 19:36:00+03:00")]
    [InlineData("not a date")]
    public void Parse_InvalidEta_IsRejected(string eta)
    {
        var provider = CreateProvider(new FakeHandler(HttpStatusCode.OK, "{}"), new FakeClock(Now));
        var json = $$$"""{"errors":null,"visits":{"3490":[{"line_name":"4","eta":"{{{eta}}}","timestamp":"2026-10-20 19:25:00+03:00"}]}}""";

        var arrivals = provider.Parse(StopCode, Encoding.UTF8.GetBytes(json), Now);

        Assert.Empty(arrivals);
    }

    [Fact]
    public void Parse_FutureObservedAt_FallsBackToNow()
    {
        var provider = CreateProvider(new FakeHandler(HttpStatusCode.OK, "{}"), new FakeClock(Now));
        const string json = """{"errors":null,"visits":{"3490":[{"line_name":"4","eta":"2026-10-20 19:36:00+03:00","timestamp":"2038-01-01 00:00:00+00:00"}]}}""";

        var arrival = Assert.Single(provider.Parse(StopCode, Encoding.UTF8.GetBytes(json), Now));

        Assert.Equal(Now, arrival.ObservedAt);
    }

    [Fact]
    public void Parse_ErrorsPayload_Throws()
    {
        var provider = CreateProvider(new FakeHandler(HttpStatusCode.OK, "{}"), new FakeClock(Now));
        var json = """{"errors":["Invalid stop code 3490\n"]}""";

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => provider.Parse(StopCode, Encoding.UTF8.GetBytes(json), Now));
    }

    [Fact]
    public async Task GetArrivals_SecondCallWithinCacheWindow_DoesNotHitTheNetwork()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Fixture());
        var provider = CreateProvider(handler, new FakeClock(Now));

        var first = await provider.GetArrivalsAsync(StopCode);
        var second = await provider.GetArrivalsAsync(StopCode);

        Assert.Equal(ProviderStatus.Ok, first.Status);
        Assert.Same(first, second);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetArrivals_SendsJsonAcceptHeader()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Fixture());
        var provider = CreateProvider(handler, new FakeClock(Now));

        await provider.GetArrivalsAsync(StopCode);

        Assert.Equal("application/json", handler.LastAccept);
        Assert.Equal("https://curlbus.app/3490", handler.LastUri);
    }

    [Fact]
    public async Task GetArrivals_FailuresAtThreeDifferentStops_OpenTheCircuitForEveryStop()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, "oops");
        var provider = CreateProvider(handler, new FakeClock(Now));

        foreach (var stop in new[] { "1111", "2222", "3333" })
        {
            Assert.Equal(ProviderStatus.Unavailable, (await provider.GetArrivalsAsync(stop)).Status);
        }

        var paused = await provider.GetArrivalsAsync(StopCode);

        Assert.Equal(ProviderStatus.CircuitOpen, paused.Status);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task GetArrivals_OneBrokenStop_IsNotRetriedEverySecondAndDoesNotPauseOtherStops()
    {
        // Curlbus returns 500 for some stop codes (seen live for 4237) while other stops work.
        var handler = new FakeHandler(HttpStatusCode.InternalServerError, "oops");
        var provider = CreateProvider(handler, new FakeClock(Now));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(ProviderStatus.Unavailable, (await provider.GetArrivalsAsync("4237")).Status);
        }

        Assert.Equal(1, handler.CallCount);
        Assert.NotEqual(ProviderStatus.CircuitOpen, (await provider.GetArrivalsAsync(StopCode)).Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("34 90")]
    [InlineData("../admin")]
    [InlineData("1234567890123")]
    public async Task GetArrivals_InvalidStopCode_NeverCallsTheNetwork(string stopCode)
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Fixture());
        var provider = CreateProvider(handler, new FakeClock(Now));

        var result = await provider.GetArrivalsAsync(stopCode);

        Assert.Equal(ProviderStatus.Unavailable, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "curlbus-stop-3490.json"), Encoding.UTF8);

    private static CurlbusArrivalsProvider CreateProvider(FakeHandler handler, IClock clock) =>
        new(new FakeHttpClientFactory(handler), clock, NullLogger<CurlbusArrivalsProvider>.Instance);

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? LastAccept { get; private set; }
        public string? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastAccept = request.Headers.Accept.ToString();
            LastUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
