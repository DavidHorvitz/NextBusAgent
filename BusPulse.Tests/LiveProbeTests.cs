using System.IO;
using System.Net.Http;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using BusPulse.Services.Stops;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>
/// Manual probes against the real services; excluded from CI with <c>--filter "Category!=Live"</c>.
/// Run with <c>dotnet test --filter "Category=Live"</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveProbeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Curlbus_ReturnsCurrentArrivalsForABusyStop()
    {
        var provider = new CurlbusArrivalsProvider(new RealFactory(), new SystemClock(), NullLogger<CurlbusArrivalsProvider>.Instance);

        var result = await provider.GetArrivalsAsync("3490");

        Assert.True(result.Status is ProviderStatus.Ok or ProviderStatus.NoData, $"Curlbus status: {result.Status} {result.Detail}");
        Assert.All(result.Arrivals, arrival => Assert.True(arrival.IsLive));
    }

    [Fact]
    public async Task StrideStops_ReturnsToday()
    {
        var directory = Path.Combine(Path.GetTempPath(), "buspulse-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new StrideStopCatalog(new RealFactory(), new SystemClock(), NullLogger<StrideStopCatalog>.Instance, directory);

            var stops = await catalog.GetStopsAsync();

            Assert.True(stops.Count > 10_000, $"Only {stops.Count} stops were returned.");
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
    public async Task Aggregator_ReturnsCurrentArrivalsFromTheRealServices()
    {
        var clock = new SystemClock();
        var aggregator = new LiveDataAggregator(
            [new CurlbusArrivalsProvider(new RealFactory(), clock, NullLogger<CurlbusArrivalsProvider>.Instance)],
            new StrideScheduleProvider(new RealFactory(), clock, NullLogger<StrideScheduleProvider>.Instance),
            clock,
            NullLogger<LiveDataAggregator>.Instance);

        var result = await aggregator.GetArrivalsAsync("3490");

        Assert.NotEmpty(result.Arrivals);
        Assert.All(result.Arrivals, arrival => Assert.InRange(arrival.Eta, clock.UtcNow.AddMinutes(-2), clock.UtcNow.AddHours(6)));
    }

    [Fact]
    public async Task StrideLatestFirstOrderWorks_AndReportsWhetherCurlbusTripIdsMatch()
    {
        var clock = new SystemClock();
        var directory = Path.Combine(Path.GetTempPath(), "buspulse-live-" + Guid.NewGuid().ToString("N"));
        var catalog = new StrideStopCatalog(new RealFactory(), clock, NullLogger<StrideStopCatalog>.Instance, directory);
        var stop = Assert.Single(await catalog.WithCurrentIdsAsync([new Stop("3490", "probe", null, 32.104812, 34.846465, Id: 1)]));
        var schedule = new BusPulse.Services.Trips.StrideRideScheduleSource(new RealFactory(), NullLogger<BusPulse.Services.Trips.StrideRideScheduleSource>.Instance);
        var now = clock.UtcNow;

        var latest = await schedule.GetStopDeparturesAsync(stop.Id, now.AddMinutes(-30), now.AddMinutes(90), latestFirst: true);
        var live = await new CurlbusArrivalsProvider(new RealFactory(), clock, NullLogger<CurlbusArrivalsProvider>.Instance).GetArrivalsAsync("3490");

        Assert.NotEmpty(latest);
        Assert.Equal(latest.OrderByDescending(row => row.Time).Select(row => row.Time), latest.Select(row => row.Time));
        Assert.All(latest, row => Assert.False(string.IsNullOrEmpty(row.JourneyRef)));
        // The two sources often name trips from different feed versions, so a match is reported, not required;
        // the planner falls back to time matching when none is equal.
        var refs = latest.Select(row => row.JourneyRef).ToHashSet();
        var tripIds = live.Arrivals.Select(arrival => arrival.TripRef).OfType<string>().ToArray();
        output.WriteLine($"Curlbus trip ids: {string.Join(",", tripIds)}; equal to a Stride journey ref: {tripIds.Count(refs.Contains)}");
    }

    private sealed class RealFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(150) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BusPulse-Tests/0.1 (+https://github.com/DavidHorvitz/NextBusAgent)");
            return client;
        }
    }
}
