using System.Net.Http;
using BusPulse.Services;
using BusPulse.Services.Geocoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>One real request to Nominatim; excluded from CI. Run with <c>dotnet test --filter "Category=Live"</c>.</summary>
[Trait("Category", "Live")]
public sealed class NominatimLiveProbeTests
{
    [Fact]
    public async Task Nominatim_FindsAnAddressInTelAviv()
    {
        var geocoder = new NominatimGeocoder(new RealFactory(), new SystemClock(), new MemorySettings(), NullLogger<NominatimGeocoder>.Instance);

        var result = await geocoder.SearchAsync("רחוב הרצל 10 תל אביב");

        Assert.Equal(GeocodeStatus.Ok, result.Status);
        Assert.Contains(result.Places, place => place.Label.StartsWith("הרצל 10", StringComparison.Ordinal));
    }

    private sealed class RealFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }
}
