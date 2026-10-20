using System.IO;
using System.Text;
using BusPulse.Services.LiveData;
using Xunit;

namespace BusPulse.Tests;

public sealed class StrideScheduleProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T16:30:00+00:00");

    [Fact]
    public void ParseStopId_RealFixture_ReturnsTheStopId()
    {
        var stopId = StrideScheduleProvider.ParseStopId(Bytes("stride-stop-3490.json"));

        Assert.Equal(49480910, stopId);
    }

    [Fact]
    public void ParseStopId_EmptyArray_ReturnsNull()
    {
        Assert.Null(StrideScheduleProvider.ParseStopId("[]"u8));
    }

    [Fact]
    public void ParseRideStops_RealFixture_ReturnsScheduledArrivals()
    {
        var arrivals = StrideScheduleProvider.ParseRideStops("3490", Bytes("stride-ride-stops-3490.json"), Now);

        Assert.NotEmpty(arrivals);
        Assert.All(arrivals, arrival =>
        {
            Assert.False(arrival.IsLive);
            Assert.Equal(ArrivalSource.Schedule, arrival.Source);
            Assert.False(string.IsNullOrWhiteSpace(arrival.LineName));
        });
        Assert.Equal(arrivals.OrderBy(a => a.Eta).ToArray(), arrivals);
    }

    [Fact]
    public void ParseRideStops_RowsWithoutLineOrTime_AreSkipped()
    {
        const string json = """[{"gtfs_route__route_short_name":"4"},{"arrival_time":"2026-10-20T16:40:00+00:00"},{"gtfs_route__route_short_name":"4","arrival_time":"nope"}]""";

        Assert.Empty(StrideScheduleProvider.ParseRideStops("3490", Encoding.UTF8.GetBytes(json), Now));
    }

    [Fact]
    public void ParseRideStops_NonArrayRoot_Throws()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            StrideScheduleProvider.ParseRideStops("3490", "{}"u8, Now));
    }

    [Theory]
    [InlineData("לח''י/מבצע קדש-בני ברק<->קהילות יעקב/רבי יוסף צבי רימון-בני ברק-10", "קהילות יעקב/רבי יוסף צבי רימון")]
    [InlineData("ת. רכבת יבנה מערב-יבנה<->ת. רכבת יבנה מזרח-יבנה-1#", "ת. רכבת יבנה מזרח")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void DestinationFromRouteName_ExtractsTheDestination(string? routeName, string? expected)
    {
        Assert.Equal(expected, StrideScheduleProvider.DestinationFromRouteName(routeName));
    }

    private static byte[] Bytes(string fixture) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
}
