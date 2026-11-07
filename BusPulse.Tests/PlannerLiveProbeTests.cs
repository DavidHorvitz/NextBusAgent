using System.Diagnostics;
using System.Net.Http;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace BusPulse.Tests;

/// <summary>
/// Runs the real planner against the real services and prints every HTTP call, so a failing plan can be explained
/// (which request, which status, how long). Manual: <c>dotnet test --filter "Category=Live"</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class PlannerLiveProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Planner_AgainstTheRealServices_ReturnsOptionsOrExplainsWhyNot()
    {
        var clock = new SystemClock();
        var factory = new LoggingFactory(output);
        var catalog = new StrideStopCatalog(factory, clock, NullLogger<StrideStopCatalog>.Instance);
        var stops = await catalog.GetStopsAsync();
        var origin = stops.First(stop => stop.Code == "26583");
        output.WriteLine($"origin {origin.Name} ({origin.Latitude},{origin.Longitude}); catalog has {stops.Count} stops");

        var aggregator = new LiveDataAggregator(
            [new CurlbusArrivalsProvider(factory, clock, NullLogger<CurlbusArrivalsProvider>.Instance)],
            new StrideScheduleProvider(factory, clock, NullLogger<StrideScheduleProvider>.Instance, catalog),
            clock,
            NullLogger<LiveDataAggregator>.Instance);
        var planner = new TripPlannerService(
            catalog, new StrideRideScheduleSource(factory, NullLogger<StrideRideScheduleSource>.Instance), aggregator, clock, NullLogger<TripPlannerService>.Instance);

        TripPlan? plan = null;
        foreach (var name in new[] { "מסוף אור יהודה", "מסוף כרמלית", "קניון איילון", "ת.מרכזית תל אביב", "מסוף עתידים" })
        {
            var destination = stops.FirstOrDefault(stop => stop.Name.Contains(name, StringComparison.Ordinal));
            if (destination is null)
            {
                output.WriteLine($"no stop named like {name}");
                continue;
            }

            var watch = Stopwatch.StartNew();
            plan = await planner.PlanAsync(new TripRequest(origin.Latitude, origin.Longitude, destination.Latitude, destination.Longitude));
            output.WriteLine($"PLAN to {destination.Name}: {plan.Status}, {plan.Options.Count} options in {watch.Elapsed.TotalSeconds:0.0}s");
            foreach (var option in plan.Options.Take(3))
            {
                output.WriteLine($"  line {option.Line} leave {option.LeaveBy.ToLocalTime():HH:mm} bus {option.BusDeparture.ToLocalTime():HH:mm} live={option.IsLive}");
            }
        }

        Assert.NotNull(plan);
        Assert.True(plan.Status is TripPlanStatus.Ok or TripPlanStatus.NoOptions, $"Unexpected status {plan.Status}");
    }

    private sealed class LoggingFactory(ITestOutputHelper output) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new LoggingHandler(output, new HttpClientHandler())) { Timeout = TimeSpan.FromSeconds(150) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BusPulse-Tests/0.1 (+https://github.com/DavidHorvitz/NextBusAgent)");
            return client;
        }
    }

    private sealed class LoggingHandler(ITestOutputHelper output, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private static int _inFlight;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var started = Clock.Elapsed.TotalSeconds;
            var concurrent = Interlocked.Increment(ref _inFlight);
            var path = request.RequestUri!.AbsolutePath + (request.RequestUri.Query.Length > 90 ? request.RequestUri.Query[..90] + "..." : request.RequestUri.Query);
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                output.WriteLine($"[{started,6:0.0}s +{Clock.Elapsed.TotalSeconds - started,5:0.0}s inflight={concurrent}] {(int)response.StatusCode} {request.RequestUri.Host}{path}");
                return response;
            }
            catch (Exception exception)
            {
                output.WriteLine($"[{started,6:0.0}s +{Clock.Elapsed.TotalSeconds - started,5:0.0}s inflight={concurrent}] {exception.GetType().Name} {request.RequestUri.Host}{path}");
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
