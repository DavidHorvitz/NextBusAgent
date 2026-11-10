using System.IO;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using BusPulse.Services.Trips.Gtfs;
using BusPulse.Views.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class RouteColorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T19:26:00+03:00");

    private static byte[] Feed() => GtfsScratch.Zip(new Dictionary<string, string>
    {
        ["agency.txt"] = "agency_id,agency_name\n3,דן\n5,אגד\n",
        ["routes.txt"] = "route_id,agency_id,route_short_name,route_long_name,route_color\n" +
            "1,3,4,a,FF9933\n" +   // coloured
            "2,3,66,b,\n" +         // no colour: stays neutral
            "3,5,18,c,3399FF\n" +   // two routes, two colours: ambiguous, dropped
            "4,5,18,d,33CC33\n" +
            "5,5,7,e,#9933ff\n"     // normalised
    });

    [Fact]
    public void Catalog_ReadsFeedColours_AndDropsAmbiguousOrMissingOnes()
    {
        using var scratch = new GtfsScratch();
        var zip = scratch.PlaceCached(Feed(), Now, TimeSpan.Zero);
        var catalog = new RouteColorCatalog(null, scratch.Directory);

        Assert.True(catalog.RefreshFromZip(zip));

        Assert.Equal("FF9933", catalog.Find("4", "דן"));
        Assert.Null(catalog.Find("66", "דן"));
        Assert.Null(catalog.Find("18", "אגד"));
        Assert.Equal("9933FF", catalog.Find("7", "אגד"));
        Assert.Null(catalog.Find("4", "אגד"));
        Assert.Null(catalog.Find("4", null));
    }

    [Fact]
    public void Catalog_LoadsFromACachedFeedOnFirstUse_AndFromItsOwnFileAfterwards()
    {
        using var scratch = new GtfsScratch();
        scratch.PlaceCached(Feed(), Now, TimeSpan.Zero);

        Assert.Equal("FF9933", new RouteColorCatalog(null, scratch.Directory).Find("4", "דן"));
        File.Delete(Path.Combine(scratch.Directory, "gtfs-2026-11-07.zip"));
        Assert.Equal("FF9933", new RouteColorCatalog(null, scratch.Directory).Find("4", "דן"));
    }

    [Fact]
    public void Catalog_WithNoFeed_ReturnsNoColour()
    {
        using var scratch = new GtfsScratch();
        Assert.Null(new RouteColorCatalog(null, scratch.Directory).Find("4", "דן"));
    }

    [Fact]
    public async Task Aggregator_AddsTheRouteColourToLiveAndScheduledArrivals()
    {
        var live = new Arrival("1", "4", "דן", "x", Now.AddMinutes(5), true, ArrivalSource.Curlbus, Now);
        var aggregator = new LiveDataAggregator(
            [new StubProvider(live)], null, new Clock(), NullLogger<LiveDataAggregator>.Instance, new StubColors());

        var result = await aggregator.GetArrivalsAsync("1");

        Assert.Equal("FF9933", Assert.Single(result.Arrivals).RouteColor);
    }

    [Fact]
    public void LineBadge_NumberAlwaysReadsLeftToRight_SoDigitsComeFirst() =>
        Wpf.Run(() =>
        {
            var badge = new LineBadge { LineName = "54א", OperatorName = "אגד", FlowDirection = System.Windows.FlowDirection.RightToLeft };
            badge.Resources.MergedDictionaries.Add(Wpf.Theme());
            badge.ApplyTemplate();
            var number = (System.Windows.Controls.TextBlock)badge.Template.FindName("Number", badge);
            Assert.Equal(System.Windows.FlowDirection.LeftToRight, number.FlowDirection);
        });

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class StubColors : IRouteColors
    {
        public string? Find(string? line, string? operatorName) => line == "4" && operatorName == "דן" ? "FF9933" : null;
    }

    private sealed class StubProvider(Arrival arrival) : ILiveArrivalsProvider
    {
        public ArrivalSource Source => ArrivalSource.Curlbus;

        public Task<ProviderResult> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderResult(ProviderStatus.Ok, [arrival]));
    }
}
