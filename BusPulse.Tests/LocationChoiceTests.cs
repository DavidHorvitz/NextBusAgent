using BusPulse.Services.Geocoding;
using BusPulse.ViewModels;
using Xunit;

namespace BusPulse.Tests;

public sealed class LocationChoiceTests
{
    [Fact]
    public void ToString_IsTheDisplayName_SoAScreenReaderReadsTheAddressNotTheTypeName()
    {
        var choice = new LocationChoice("הרצל 10, תל־אביב–יפו", address: new GeocodedPlace("הרצל 10, תל־אביב–יפו", 32.06, 34.77));

        Assert.Equal("הרצל 10, תל־אביב–יפו", choice.ToString());

        choice.DisplayName = "Herzl 10, Tel Aviv";
        Assert.Equal("Herzl 10, Tel Aviv", choice.ToString());
    }
}
