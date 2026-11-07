using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.Geocoding;
using BusPulse.Services.Secrets;
using BusPulse.ViewModels;
using Xunit;

namespace BusPulse.Tests;

public sealed class SettingsAddressTests
{
    private static readonly GeocodedPlace Dizengoff = new("Dizengoff Street 50, Tel Aviv", 32.08, 34.78);
    private static readonly GeocodedPlace Allenby = new("Allenby Street 10, Tel Aviv", 32.07, 34.77);

    private sealed class FakeSecrets(string describe) : ISecretProvider
    {
        public string Describe => describe;

        public Task<string?> GetAsync(string name, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class Fixture
    {
        public MemorySettings Settings { get; } = new();
        public FakeGeocoder Geocoder { get; } = new();
        public SettingsViewModel Vm { get; }

        public Fixture(string secrets = "environment variables", Action<AppConfig>? configure = null)
        {
            configure?.Invoke(Settings.Config);
            Vm = new SettingsViewModel(
                Settings, new SavedPlaceCollection(Settings), new TemplateLocalizer(), [], new ShellAndSettingsTests.FakeSchedule(),
                new FakeHistory(), new RecordingNotifier(), new FakeCalendar(), Geocoder, new FakeSecrets(secrets));
        }

        public void Found(params GeocodedPlace[] places) =>
            Geocoder.Result = new GeocodeResult(GeocodeStatus.Ok, places);
    }

    [Fact]
    public async Task Search_FillsResultsSelectsFirstAndSuggestsItsLabelAsName()
    {
        var f = new Fixture();
        f.Found(Dizengoff, Allenby);
        f.Vm.AddressQuery = "dizengoff 50";

        await f.Vm.SearchAddressCommand.ExecuteAsync(null);

        Assert.Equal(["dizengoff 50"], f.Geocoder.Queries);
        Assert.Equal([Dizengoff, Allenby], f.Vm.AddressResults);
        Assert.Equal(Dizengoff, f.Vm.SelectedAddress);
        Assert.Equal(Dizengoff.Label, f.Vm.NewAddressPlaceName);
        Assert.Equal(string.Empty, f.Vm.AddressStatus);
    }

    [Fact]
    public async Task Search_ShowsAtMostFiveResults()
    {
        var f = new Fixture();
        f.Found(Enumerable.Range(0, 8).Select(i => new GeocodedPlace("Place " + i, 32, 34.8)).ToArray());
        f.Vm.AddressQuery = "place";

        await f.Vm.SearchAddressCommand.ExecuteAsync(null);

        Assert.Equal(5, f.Vm.AddressResults.Count);
    }

    [Fact]
    public async Task Search_KeepsNameTheUserTyped()
    {
        var f = new Fixture();
        f.Found(Dizengoff);
        f.Vm.AddressQuery = "dizengoff";
        f.Vm.NewAddressPlaceName = "Gym";

        await f.Vm.SearchAddressCommand.ExecuteAsync(null);

        Assert.Equal("Gym", f.Vm.NewAddressPlaceName);
    }

    [Theory]
    [InlineData(GeocodeStatus.Disabled, "AddressSearchOff")]
    [InlineData(GeocodeStatus.Unavailable, "AddressUnavailable")]
    [InlineData(GeocodeStatus.NoResults, "AddressNoResults")]
    [InlineData(GeocodeStatus.TooShort, "AddressTooShort")]
    public async Task Search_MapsEachGeocoderStatusToItsMessage(GeocodeStatus status, string expectedKey)
    {
        var f = new Fixture();
        f.Geocoder.Result = GeocodeResult.Of(status);
        f.Vm.AddressQuery = "somewhere";

        await f.Vm.SearchAddressCommand.ExecuteAsync(null);

        Assert.Equal(expectedKey, f.Vm.AddressStatus);
        Assert.Empty(f.Vm.AddressResults);
        Assert.Null(f.Vm.SelectedAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    public async Task Search_EmptyOrShortQuery_NeverCallsTheGeocoder(string query)
    {
        var f = new Fixture();
        f.Vm.AddressQuery = query;

        await f.Vm.SearchAddressCommand.ExecuteAsync(null);

        Assert.Empty(f.Geocoder.Queries);
        Assert.Equal("AddressTooShort", f.Vm.AddressStatus);
    }

    [Fact]
    public async Task Search_WhenSwitchedOff_NeverCallsTheGeocoder()
    {
        var f = new Fixture();
        f.Settings.Config.AddressSearchEnabled = false;
        f.Vm.AddressQuery = "dizengoff 50";

        await f.Vm.SearchAddressCommand.ExecuteAsync(null);

        Assert.Empty(f.Geocoder.Queries);
        Assert.Equal("AddressSearchOff", f.Vm.AddressStatus);
    }

    [Fact]
    public void TypingAndChangingTheSelection_NeverSearches()
    {
        var f = new Fixture();
        f.Found(Dizengoff);

        f.Vm.AddressQuery = "d";
        f.Vm.AddressQuery = "dizengoff";
        f.Vm.AddressQuery = "dizengoff 50";
        f.Vm.SelectedAddress = Allenby;
        f.Vm.NewAddressPlaceName = "x";

        Assert.Empty(f.Geocoder.Queries);
    }

    [Fact]
    public async Task Add_CreatesCoordinatePlaceAndSavesIt()
    {
        var f = new Fixture();
        f.Found(Dizengoff, Allenby);
        f.Vm.AddressQuery = "tel aviv";
        await f.Vm.SearchAddressCommand.ExecuteAsync(null);
        f.Vm.SelectedAddress = Allenby;
        f.Vm.NewAddressPlaceName = "  Gym ";

        await f.Vm.AddAddressPlaceCommand.ExecuteAsync(null);

        var place = Assert.Single(f.Vm.Places);
        Assert.Equal("Gym", place.Name);
        Assert.Equal(32.07, place.Latitude);
        Assert.Equal(34.77, place.Longitude);
        Assert.Equal(Allenby.Label, place.Address);
        Assert.Equal(Allenby.Label, place.DisplayDetail);
        Assert.Equal(string.Empty, place.StopCode);
        Assert.Equal(1, f.Settings.SaveCount);
        Assert.Same(place, Assert.Single(f.Settings.Config.SavedPlaces));
        Assert.Equal("PlaceSaved", f.Vm.AddressStatus);
        Assert.Empty(f.Vm.AddressResults);
        Assert.Equal(string.Empty, f.Vm.NewAddressPlaceName);
    }

    [Fact]
    public async Task Add_ReportsASaveFailureButKeepsThePlaceForThisSession()
    {
        var f = new Fixture();
        f.Settings.SaveSucceeds = false;
        f.Found(Dizengoff);
        f.Vm.AddressQuery = "dizengoff";
        await f.Vm.SearchAddressCommand.ExecuteAsync(null);

        await f.Vm.AddAddressPlaceCommand.ExecuteAsync(null);

        Assert.Single(f.Vm.Places);
        Assert.Equal("PlaceSaveFailed", f.Vm.AddressStatus);
    }

    [Fact]
    public async Task Add_WithoutAResult_DoesNothing()
    {
        var f = new Fixture();
        f.Vm.NewAddressPlaceName = "Gym";

        await f.Vm.AddAddressPlaceCommand.ExecuteAsync(null);

        Assert.Empty(f.Vm.Places);
        Assert.Equal(0, f.Settings.SaveCount);
        Assert.Equal("AddressPickFirst", f.Vm.AddressStatus);
    }

    [Fact]
    public async Task Add_DuplicateNameIsRejectedCaseInsensitively()
    {
        var f = new Fixture(configure: config => config.SavedPlaces = [new SavedPlace { Name = "Gym", StopCode = "12345" }]);
        f.Found(Dizengoff);
        f.Vm.AddressQuery = "dizengoff";
        await f.Vm.SearchAddressCommand.ExecuteAsync(null);
        f.Vm.NewAddressPlaceName = "gym";

        await f.Vm.AddAddressPlaceCommand.ExecuteAsync(null);

        Assert.Single(f.Vm.Places);
        Assert.Equal("PlaceAlreadyExists", f.Vm.AddressStatus);
        Assert.Equal(0, f.Settings.SaveCount);
    }

    [Fact]
    public async Task Add_StopsAtThirtyPlaces()
    {
        var f = new Fixture(configure: config =>
            config.SavedPlaces = Enumerable.Range(0, 30).Select(i => new SavedPlace { Name = "P" + i, StopCode = "1" + i }).ToList());
        f.Found(Dizengoff);
        f.Vm.AddressQuery = "dizengoff";
        await f.Vm.SearchAddressCommand.ExecuteAsync(null);

        await f.Vm.AddAddressPlaceCommand.ExecuteAsync(null);

        Assert.Equal(30, f.Vm.Places.Count);
        Assert.Equal("PlaceLimitReached", f.Vm.AddressStatus);
    }

    [Fact]
    public async Task Add_OutsideIsraelIsRejectedAsInvalid()
    {
        var f = new Fixture();
        f.Found(new GeocodedPlace("Paris", 48.85, 2.35));
        f.Vm.AddressQuery = "paris";
        await f.Vm.SearchAddressCommand.ExecuteAsync(null);

        await f.Vm.AddAddressPlaceCommand.ExecuteAsync(null);

        Assert.Empty(f.Vm.Places);
        Assert.Equal("InvalidPlaceMessage", f.Vm.AddressStatus);
    }

    [Fact]
    public async Task StopCodeFlow_StillWorks()
    {
        var f = new Fixture();
        f.Vm.NewPlaceName = "Home";
        f.Vm.NewPlaceStopCode = "12345";

        await f.Vm.AddPlaceCommand.ExecuteAsync(null);

        var place = Assert.Single(f.Vm.Places);
        Assert.Equal("12345", place.DisplayDetail);
        Assert.Equal("PlaceSaved", f.Vm.PlaceStatus);
    }

    [Fact]
    public async Task Toggle_PersistsAndReportsASaveFailure()
    {
        var f = new Fixture();
        Assert.True(f.Vm.AddressSearchEnabled);

        f.Vm.AddressSearchEnabled = false;
        await Task.Yield();

        Assert.False(f.Settings.Config.AddressSearchEnabled);
        Assert.Equal(1, f.Settings.SaveCount);

        f.Settings.SaveSucceeds = false;
        f.Vm.AddressSearchEnabled = true;
        await Task.Yield();

        Assert.True(f.Settings.Config.AddressSearchEnabled);
        Assert.Equal("SettingsSaveFailed", f.Vm.SaveStatus);
    }

    [Fact]
    public void Attribution_UsesTheGeocoderConstant() =>
        Assert.Equal(NominatimGeocoder.Attribution, new Fixture().Vm.AddressAttribution);

    [Theory]
    [InlineData("Infisical (environment variables as fallback)", "SecretsSourceInfisical")]
    [InlineData("environment variables", "SecretsSourceEnvironment")]
    [InlineData("Infisical (not configured)", "SecretsSourceNone")]
    [InlineData("", "SecretsSourceNone")]
    public void SecretsSource_MapsDescribeToAMessage(string describe, string expectedKey) =>
        Assert.Equal(expectedKey, new Fixture(describe).Vm.SecretsSourceStatus);
}
