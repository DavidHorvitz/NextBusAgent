using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BusPulse.Configuration;
using BusPulse.Services;
using BusPulse.Services.Geocoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class NominatimGeocoderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T10:00:00+02:00");

    [Fact]
    public async Task Search_HerzlFixture_ReturnsShortLabelsInsideIsrael()
    {
        var (geocoder, handler, _) = Create(Fixture("herzl-10-tel-aviv"));

        var result = await geocoder.SearchAsync("הרצל 10 תל אביב");

        Assert.Equal(GeocodeStatus.Ok, result.Status);
        Assert.InRange(result.Places.Count, 1, 5);
        Assert.Equal("הרצל 10, תל־אביב–יפו", result.Places[0].Label);
        Assert.Equal(32.062416, result.Places[0].Latitude, 5);
        Assert.Equal(34.769931, result.Places[0].Longitude, 5);
        Assert.All(result.Places, place =>
        {
            Assert.True(place.Label.Length < 60, place.Label);
            Assert.DoesNotContain("ישראל", place.Label);
        });
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Search_RequestCarriesPolicyParametersAndUserAgent()
    {
        var (geocoder, handler, _) = Create(Fixture("herzl-10-tel-aviv"));

        await geocoder.SearchAsync("  רחוב   הרצל 10  תל אביב ");

        var uri = handler.LastUri!;
        Assert.StartsWith("https://nominatim.openstreetmap.org/search?q=", uri.AbsoluteUri);
        Assert.Equal("הרצל 10 תל אביב", System.Web.HttpUtility.ParseQueryString(uri.Query)["q"]);
        Assert.Contains("format=jsonv2", uri.Query);
        Assert.Contains("addressdetails=1", uri.Query);
        Assert.Contains("countrycodes=il", uri.Query);
        Assert.Contains("limit=5", uri.Query);
        Assert.Contains("accept-language=he", uri.Query);
        Assert.Equal(NominatimGeocoder.UserAgent, handler.LastUserAgent);
        Assert.DoesNotContain("@", NominatimGeocoder.UserAgent.Replace("(+", string.Empty));
    }

    [Fact]
    public async Task Search_DizengoffFixture_MergesObjectsAtTheSameAddress()
    {
        var (geocoder, _, _) = Create(Fixture("dizengoff-50-tel-aviv"));

        var result = await geocoder.SearchAsync("דיזנגוף 50 תל אביב");

        Assert.Equal(GeocodeStatus.Ok, result.Status);
        Assert.Single(result.Places);
        Assert.Equal("דיזנגוף 50, תל־אביב–יפו", result.Places[0].Label);
    }

    [Fact]
    public async Task Search_JerusalemAddress_UsesRoadHouseNumberAndCity()
    {
        var (geocoder, _, _) = Create(Fixture("ben-yehuda-5-jerusalem"));

        var result = await geocoder.SearchAsync("בן יהודה 5 ירושלים");

        Assert.Equal(GeocodeStatus.Ok, result.Status);
        Assert.Equal("בן יהודה 5, ירושלים", result.Places[0].Label);
    }

    [Fact]
    public async Task Search_NamedPlaceWithoutHouseNumber_UsesTheName()
    {
        var (geocoder, _, _) = Create(Fixture("western-wall"));

        var result = await geocoder.SearchAsync("הכותל המערבי ירושלים");

        Assert.Equal(GeocodeStatus.Ok, result.Status);
        Assert.Contains(result.Places, place => place.Label == "הכותל המערבי, ירושלים");
    }

    [Fact]
    public async Task Search_EnglishQuery_ReturnsHebrewLabels()
    {
        var (geocoder, _, _) = Create(Fixture("english-ben-yehuda"));

        var result = await geocoder.SearchAsync("Ben Yehuda Street 5 Jerusalem");

        Assert.Equal(GeocodeStatus.Ok, result.Status);
        Assert.Equal(3, result.Places.Count);
        Assert.All(result.Places, place => Assert.Equal("בן יהודה, ירושלים", place.Label));
    }

    [Fact]
    public async Task Search_SmallTownUsesTownField()
    {
        var (geocoder, _, _) = Create(Fixture("small-town"));

        var result = await geocoder.SearchAsync("המייסדים 10 זכרון יעקב");

        Assert.Equal(GeocodeStatus.Ok, result.Status);
        Assert.All(result.Places, place => Assert.EndsWith(", זכרון יעקב", place.Label));
    }

    [Fact]
    public async Task Search_EmptyArray_IsNoResults()
    {
        var (geocoder, _, _) = Create(Fixture("no-result"));

        var result = await geocoder.SearchAsync("אאאבבבגגגדדד 99999 זזזזז");

        Assert.Equal(GeocodeStatus.NoResults, result.Status);
        Assert.Empty(result.Places);
    }

    [Fact]
    public void RehovFixture_ConfirmsWhyTheLongFormMustBeNormalized()
    {
        Assert.Equal("[]", Fixture("with-rehov").Trim());
    }

    [Theory]
    [InlineData("רחוב הרצל 10 תל אביב", "הרצל 10 תל אביב")]
    [InlineData("רח' הרצל 10", "הרצל 10")]
    [InlineData("רח הרצל 10", "הרצל 10")]
    [InlineData("Street Herzl 10", "Herzl 10")]
    [InlineData("St. Herzl 10", "Herzl 10")]
    [InlineData("  הרצל    10 \t תל אביב  ", "הרצל 10 תל אביב")]
    [InlineData("רחובות", "רחובות")]
    [InlineData("Stanley 4", "Stanley 4")]
    public void NormalizeQuery_TrimsCollapsesAndStripsLeadingStreetWord(string input, string expected) =>
        Assert.Equal(expected, NominatimGeocoder.NormalizeQuery(input));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ab")]
    [InlineData("רחוב א")]
    public async Task Search_TooShort_SendsNothing(string query)
    {
        var (geocoder, handler, _) = Create("[]");

        var result = await geocoder.SearchAsync(query);

        Assert.Equal(GeocodeStatus.TooShort, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Search_TooLong_SendsNothing()
    {
        var (geocoder, handler, _) = Create("[]");

        var result = await geocoder.SearchAsync(new string('א', 121));

        Assert.Equal(GeocodeStatus.NoResults, result.Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Search_SwitchedOff_ReturnsDisabledWithoutHttpCall()
    {
        var (geocoder, handler, settings) = Create(Fixture("herzl-10-tel-aviv"));
        settings.Config.AddressSearchEnabled = false;

        var result = await geocoder.SearchAsync("הרצל 10 תל אביב");

        Assert.Equal(GeocodeStatus.Disabled, result.Status);
        Assert.Equal(0, handler.CallCount);

        settings.Config.AddressSearchEnabled = true;
        Assert.Equal(GeocodeStatus.Ok, (await geocoder.SearchAsync("הרצל 10 תל אביב")).Status);
    }

    [Fact]
    public async Task Search_SameQueryTwice_MakesOneHttpCall()
    {
        var (geocoder, handler, _) = Create(Fixture("herzl-10-tel-aviv"));

        var first = await geocoder.SearchAsync("הרצל 10 תל אביב");
        var second = await geocoder.SearchAsync("רחוב  הרצל 10 תל אביב ");

        Assert.Equal(1, handler.CallCount);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task Search_CacheExpiresAfter24Hours()
    {
        var (geocoder, handler, _, clock) = CreateWithClock(Fixture("herzl-10-tel-aviv"));

        await geocoder.SearchAsync("הרצל 10 תל אביב");
        clock.UtcNow += TimeSpan.FromHours(23);
        await geocoder.SearchAsync("הרצל 10 תל אביב");
        Assert.Equal(1, handler.CallCount);

        clock.UtcNow += TimeSpan.FromHours(2);
        await geocoder.SearchAsync("הרצל 10 תל אביב");
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task Search_CacheIsBoundedAtOneHundredEntries()
    {
        var (geocoder, handler, _, clock) = CreateWithClock("[]");

        for (var i = 0; i < 101; i++)
        {
            clock.UtcNow += TimeSpan.FromSeconds(2);
            await geocoder.SearchAsync($"query number {i}");
        }

        Assert.Equal(101, handler.CallCount);
        await geocoder.SearchAsync("query number 100");
        Assert.Equal(101, handler.CallCount);
        await geocoder.SearchAsync("query number 0");
        Assert.Equal(102, handler.CallCount);
    }

    [Fact]
    public async Task Search_TwoDifferentQueriesInTheSameInstant_AreSpacedByOneSecond()
    {
        var delays = new List<TimeSpan>();
        var handler = new FakeHandler(HttpStatusCode.OK, "[]");
        var clock = new FixedClock(Now);
        var geocoder = new NominatimGeocoder(new FakeFactory(handler), clock, new MemorySettings(), NullLogger<NominatimGeocoder>.Instance,
            (span, _) =>
            {
                delays.Add(span);
                clock.UtcNow += span;
                return Task.CompletedTask;
            });

        await geocoder.SearchAsync("first query");
        await geocoder.SearchAsync("second query");
        clock.UtcNow += TimeSpan.FromSeconds(5);
        await geocoder.SearchAsync("third query");

        Assert.Equal(3, handler.CallCount);
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task Search_ServerError_IsUnavailableAndNotCached()
    {
        var (geocoder, handler, _) = Create("{}", HttpStatusCode.ServiceUnavailable);

        var first = await geocoder.SearchAsync("הרצל 10 תל אביב");
        var second = await geocoder.SearchAsync("הרצל 10 תל אביב");

        Assert.Equal(GeocodeStatus.Unavailable, first.Status);
        Assert.Equal(GeocodeStatus.Unavailable, second.Status);
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData("{\"error\":\"rate limited\"}")]
    [InlineData("not json")]
    [InlineData("")]
    public async Task Search_MalformedBody_IsUnavailable(string body)
    {
        var (geocoder, _, _) = Create(body);

        Assert.Equal(GeocodeStatus.Unavailable, (await geocoder.SearchAsync("הרצל 10 תל אביב")).Status);
    }

    [Fact]
    public async Task Search_NetworkFailure_IsUnavailable()
    {
        var (geocoder, _, _) = Create("[]", exception: new HttpRequestException("down"));

        Assert.Equal(GeocodeStatus.Unavailable, (await geocoder.SearchAsync("הרצל 10 תל אביב")).Status);
    }

    [Fact]
    public async Task Search_HttpClientTimeout_IsUnavailableNotCancellation()
    {
        var (geocoder, _, _) = Create("[]", exception: new TaskCanceledException("timeout", new TimeoutException()));

        Assert.Equal(GeocodeStatus.Unavailable, (await geocoder.SearchAsync("הרצל 10 תל אביב")).Status);
    }

    [Fact]
    public async Task Search_CallerCancellation_Throws()
    {
        var (geocoder, _, _) = Create("[]");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => geocoder.SearchAsync("הרצל 10 תל אביב", cts.Token));
    }

    [Fact]
    public async Task Search_OversizedResponse_IsUnavailable()
    {
        var (geocoder, _, _) = Create("[" + new string(' ', 300 * 1024) + "]");

        Assert.Equal(GeocodeStatus.Unavailable, (await geocoder.SearchAsync("הרצל 10 תל אביב")).Status);
    }

    [Fact]
    public async Task Search_InvalidConfiguredAddress_IsUnavailableWithoutHttpCall()
    {
        var (geocoder, handler, settings) = Create("[]");
        settings.Config.GeocoderBaseUrl = "http://insecure.example/";

        Assert.Equal(GeocodeStatus.Unavailable, (await geocoder.SearchAsync("הרצל 10 תל אביב")).Status);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Search_ConfiguredServer_IsUsed()
    {
        var (geocoder, handler, settings) = Create("[]");
        settings.Config.GeocoderBaseUrl = "https://geo.example.org/nominatim";

        await geocoder.SearchAsync("הרצל 10 תל אביב");

        Assert.StartsWith("https://geo.example.org/nominatim/search?", handler.LastUri!.AbsoluteUri);
    }

    [Fact]
    public void Parse_RejectsPlacesOutsideIsraelAndInvalidItems()
    {
        var (geocoder, _, _) = Create("[]");
        const string json = """
            [
              {"lat":"48.8566","lon":"2.3522","name":"Paris","address":{"road":"Rue","city":"Paris"}},
              {"lat":"abc","lon":"34.78","name":"Bad"},
              {"lat":"32.07","lon":"34.78","address":{"country":"ישראל"}},
              {"lat":"32.07","lon":"34.78","name":"x","address":{"road":"אלנבי","house_number":"1","city":"תל אביב"}},
              {"lat":32.1,"lon":34.8,"address":{"road":"הירקון","village":"כפר"}}
            ]
            """;

        var places = geocoder.Parse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(["אלנבי 1, תל אביב", "הירקון, כפר"], places.Select(place => place.Label));
    }

    [Fact]
    public void Parse_CapsResultsAndStringLength()
    {
        var (geocoder, _, _) = Create("[]");
        var items = Enumerable.Range(0, 9).Select(i =>
            $$$"""{"lat":"32.0{{{i}}}","lon":"34.8","address":{"road":"רחוב {{{i}}}","city":"עיר"}}""");
        var tooLong = new string('ש', 129);
        var json = "[" + string.Join(",", items) + "]";

        Assert.Equal(5, geocoder.Parse(Encoding.UTF8.GetBytes(json)).Count);

        var longName = $$$"""[{"lat":"32.07","lon":"34.78","name":"{{{tooLong}}}","address":{"road":"אלנבי","city":"תל אביב"}}]""";
        Assert.Equal("אלנבי, תל אביב", geocoder.Parse(Encoding.UTF8.GetBytes(longName))[0].Label);
    }

    [Fact]
    public void Attribution_IsExposed() => Assert.Equal("© OpenStreetMap contributors", NominatimGeocoder.Attribution);

    [Fact]
    public void AppConfig_OldFormatJsonWithoutGeocoderMembers_LoadsWithDefaults()
    {
        const string oldJson = """{"Language":"he","ActiveProfile":"Default","PollingSeconds":20,"SavedPlaces":[]}""";
        var options = new JsonSerializerOptions { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

        var config = JsonSerializer.Deserialize<AppConfig>(oldJson, options)!;
        config.Validate();

        Assert.True(config.AddressSearchEnabled);
        Assert.Equal("https://nominatim.openstreetmap.org/", config.GeocoderBaseUrl);
        Assert.Equal("he", config.Language);
    }

    [Theory]
    [InlineData("http://nominatim.example/")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("ftp://nominatim.example/")]
    public void AppConfig_Validate_RejectsNonHttpsGeocoderAddress(string url)
    {
        var config = new AppConfig { GeocoderBaseUrl = url };

        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void AppConfig_Validate_RejectsOverlongGeocoderAddress()
    {
        var config = new AppConfig { GeocoderBaseUrl = "https://example.org/" + new string('a', 190) };

        Assert.Throws<InvalidDataException>(config.Validate);
    }

    [Fact]
    public void AppConfig_Validate_AcceptsCustomHttpsGeocoderAddress()
    {
        new AppConfig { GeocoderBaseUrl = "https://geo.example.org/", AddressSearchEnabled = false }.Validate();
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"nominatim-{name}.json"), Encoding.UTF8);

    private static (NominatimGeocoder Geocoder, FakeHandler Handler, MemorySettings Settings) Create(
        string body, HttpStatusCode status = HttpStatusCode.OK, Exception? exception = null)
    {
        var (geocoder, handler, settings, _) = CreateWithClock(body, status, exception);
        return (geocoder, handler, settings);
    }

    private static (NominatimGeocoder Geocoder, FakeHandler Handler, MemorySettings Settings, FixedClock Clock) CreateWithClock(
        string body, HttpStatusCode status = HttpStatusCode.OK, Exception? exception = null)
    {
        var handler = new FakeHandler(status, body, exception);
        var settings = new MemorySettings();
        var clock = new FixedClock(Now);
        // The fake delay advances the fake clock instead of waiting, so spacing is verified without real sleeping.
        var geocoder = new NominatimGeocoder(new FakeFactory(handler), clock, settings, NullLogger<NominatimGeocoder>.Instance,
            (span, _) =>
            {
                clock.UtcNow += span;
                return Task.CompletedTask;
            });
        return (geocoder, handler, settings, clock);
    }

    private sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(NominatimGeocoder.ClientName, name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class FakeHandler(HttpStatusCode status, string body, Exception? exception = null) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastUri = request.RequestUri;
            LastUserAgent = request.Headers.UserAgent.ToString();
            if (exception is not null)
            {
                return Task.FromException<HttpResponseMessage>(exception);
            }

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
