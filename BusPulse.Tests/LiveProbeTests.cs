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
public sealed class LiveProbeTests
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

    private sealed class RealFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BusPulse-Tests/0.1 (+https://github.com/DavidHorvitz/NextBusAgent)");
            return client;
        }
    }
}
