using System.Net;
using System.Net.Http;
using System.Text;
using BusPulse.Infrastructure;
using BusPulse.Services.Stops;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using System.IO;

namespace BusPulse.Tests;

/// <summary>Stride answers with sporadic 500/503 under load (seen 2026-11-07); one failed call must not fail a whole plan.</summary>
public sealed class TransientRetryHandlerTests
{
    private static readonly HttpStatusCode Ok = HttpStatusCode.OK;

    [Fact]
    public async Task ServerErrorsThenSuccess_ReturnsTheSuccess()
    {
        var inner = new ScriptedHandler(_ => Respond(HttpStatusCode.InternalServerError), _ => Respond(HttpStatusCode.ServiceUnavailable), _ => Respond(Ok));

        using var response = await Client(inner).GetAsync("https://example.test/a");

        Assert.Equal(Ok, response.StatusCode);
        Assert.Equal(3, inner.Requests.Count);
    }

    [Fact]
    public async Task PersistentServerError_ReturnsTheLastResponseAfterAllAttempts()
    {
        var inner = new ScriptedHandler(_ => Respond(HttpStatusCode.BadGateway));

        using var response = await Client(inner).GetAsync("https://example.test/a");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(3, inner.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ClientErrors_AreNotRetried(HttpStatusCode status)
    {
        var inner = new ScriptedHandler(_ => Respond(status));

        using var response = await Client(inner).GetAsync("https://example.test/a");

        Assert.Equal(status, response.StatusCode);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task DroppedConnectionThenSuccess_Retries()
    {
        var inner = new ScriptedHandler(_ => throw new HttpRequestException("connection reset"), _ => Respond(Ok));

        using var response = await Client(inner).GetAsync("https://example.test/a");

        Assert.Equal(Ok, response.StatusCode);
        Assert.Equal(2, inner.Requests.Count);
    }

    [Fact]
    public async Task ConnectionThatKeepsDropping_ThrowsAfterAllAttempts()
    {
        var inner = new ScriptedHandler(_ => throw new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(inner).GetAsync("https://example.test/a"));

        Assert.Equal(3, inner.Requests.Count);
    }

    [Fact]
    public async Task NonGetRequests_AreNeverRetried()
    {
        var inner = new ScriptedHandler(_ => Respond(HttpStatusCode.InternalServerError));

        using var response = await Client(inner).PostAsync("https://example.test/a", new StringContent("x"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task ACancelledCaller_IsNotRetried()
    {
        using var cancellation = new CancellationTokenSource();
        var inner = new ScriptedHandler(_ =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(inner).GetAsync("https://example.test/a", cancellation.Token));

        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task EachRetry_CarriesTheSameUserAgentAndUri()
    {
        var inner = new ScriptedHandler(_ => Respond(HttpStatusCode.InternalServerError), _ => Respond(Ok));
        var client = Client(inner);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BusPulseApp/1.0");

        using var response = await client.GetAsync("https://example.test/a?x=1");

        Assert.Equal(2, inner.Requests.Count);
        Assert.All(inner.Requests, request =>
        {
            Assert.Equal("https://example.test/a?x=1", request.Uri);
            Assert.Equal("BusPulseApp/1.0", request.UserAgent);
        });
    }

    [Fact]
    public async Task ItWaitsBetweenAttempts_WithTheConfiguredDelays()
    {
        var waits = new List<TimeSpan>();
        var inner = new ScriptedHandler(_ => Respond(HttpStatusCode.InternalServerError));
        var handler = new TransientRetryHandler(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20))
        {
            InnerHandler = inner,
            Delay = (span, _) =>
            {
                waits.Add(span);
                return Task.CompletedTask;
            }
        };

        using var response = await new HttpClient(handler).GetAsync("https://example.test/a");

        Assert.Equal([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)], waits);
    }

    [Fact]
    public async Task CatalogLookups_OneStopThatFails_IsLeftOut_AndTheOthersStillResolve()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bp-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = await EarlierDaysCatalogAsync(directory, failingCodes: ["3128"]);
            var stops = await catalog.GetStopsAsync();

            var resolved = await catalog.WithCurrentIdsAsync([stops.Single(stop => stop.Code == "3490"), stops.Single(stop => stop.Code == "3128")]);

            var only = Assert.Single(resolved);
            Assert.Equal("3490", only.Code);
            Assert.Equal(99, only.Id);
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    [Fact]
    public async Task CatalogLookups_WhenEveryLookupFails_ReportsTheFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bp-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = await EarlierDaysCatalogAsync(directory, failingCodes: ["3490", "3128"]);
            var stops = await catalog.GetStopsAsync();

            await Assert.ThrowsAsync<HttpRequestException>(() =>
                catalog.WithCurrentIdsAsync([stops.Single(stop => stop.Code == "3490"), stops.Single(stop => stop.Code == "3128")]));
        }
        finally
        {
            DeleteQuietly(directory);
        }
    }

    /// <summary>A catalog serving an earlier day's list (today's download is refused), so ids must be looked up by code.</summary>
    private static async Task<StrideStopCatalog> EarlierDaysCatalogAsync(string directory, string[] failingCodes)
    {
        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stride-stops-sample.json"));
        var clock = new FixedClock(DateTimeOffset.Parse("2026-10-20T12:00:00+00:00"));
        await new StrideStopCatalog(new Factory(new LookupHandler(fixture, [], null)), clock, NullLogger<StrideStopCatalog>.Instance, directory).GetStopsAsync();

        clock.UtcNow = DateTimeOffset.Parse("2026-11-03T12:00:00+00:00");
        const string lookup = """[{"id":99,"date":"2026-11-03","code":3490,"lat":32.104812,"lon":34.846465,"name":"תחנת שיטור/איילת השחר","city":"בני ברק"}]""";
        var catalog = new StrideStopCatalog(new Factory(new LookupHandler(null, failingCodes, lookup)), clock, NullLogger<StrideStopCatalog>.Instance, directory);
        await catalog.GetStopsAsync();
        await catalog.WhenRefreshedAsync();
        return catalog;
    }

    private static void DeleteQuietly(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HttpClient Client(ScriptedHandler inner) =>
        new(new TransientRetryHandler { InnerHandler = inner, Delay = (_, _) => Task.CompletedTask });

    private static HttpResponseMessage Respond(HttpStatusCode status) =>
        new(status) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };

    private sealed record Seen(string Uri, string? UserAgent);

    /// <summary>Plays one scripted step per call; the last step repeats.</summary>
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(request.RequestUri!.ToString(), request.Headers.UserAgent.ToString()));
            var step = steps[Math.Min(Requests.Count, steps.Length) - 1];
            return Task.FromResult(step(request));
        }
    }

    /// <summary>The bulk list (or 503 when null), and single-stop lookups that fail for the given codes.</summary>
    private sealed class LookupHandler(string? bulk, string[] failingCodes, string? lookup) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            if (!uri.Contains("code=", StringComparison.Ordinal))
            {
                return Task.FromResult(bulk is null ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(bulk));
            }

            return Task.FromResult(failingCodes.Any(code => uri.Contains($"code={code}&", StringComparison.Ordinal))
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Json(lookup ?? "[]"));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
