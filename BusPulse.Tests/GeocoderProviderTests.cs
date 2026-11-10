using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using BusPulse.Services;
using BusPulse.Services.Geocoding;
using BusPulse.Services.Secrets;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>Session 2: the 10-minute breaker, the on-disk cache, and the Google Places provider (request shape, sessions, cap).</summary>
public sealed class GeocoderProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");

    // Shapes follow the Places API (New) reference; no live key was available to record real responses.
    private const string AutocompleteJson = """
        {"suggestions":[
          {"placePrediction":{"place":"places/ChIJabc","placeId":"ChIJabc","text":{"text":"הרצל 10, תל אביב-יפו, ישראל"}}},
          {"placePrediction":{"place":"places/ChIJdef","placeId":"ChIJdef","text":{"text":"הרצל 12, תל אביב-יפו, ישראל"}}},
          {"queryPrediction":{"text":{"text":"הרצל"}}}
        ]}
        """;

    private const string DetailsJson = """{"location":{"latitude":32.0644,"longitude":34.7716}}""";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "buspulse-tests-" + Guid.NewGuid().ToString("N"));

    public GeocoderProviderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string Body, HttpRequestMessage Request)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, request.RequestUri!, body, request));
            return respond(request);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Secrets(string? key) : ISecretProvider
    {
        public List<string> Asked { get; } = [];

        public Task<string?> GetAsync(string name, CancellationToken ct)
        {
            Asked.Add(name);
            return Task.FromResult(name == SecretNames.GooglePlacesApiKey ? key : null);
        }

        public string Describe => "test";
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string NominatimBody => """[{"lat":"32.06","lon":"34.77","name":"","address":{"road":"Herzl","house_number":"10","city":"Tel Aviv"}}]""";

    // ---- circuit breaker (Nominatim) ----

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Nominatim_stops_calling_for_ten_minutes_after_429_or_403(HttpStatusCode status)
    {
        var handler = new Handler(_ => Json("{}", status));
        var clock = new FixedClock(Now);
        var geocoder = new NominatimGeocoder(new Factory(handler), clock, new MemorySettings(), NullLogger<NominatimGeocoder>.Instance,
            (span, _) => { clock.UtcNow += span; return Task.CompletedTask; });

        var first = await geocoder.SearchAsync("herzl ten");
        var second = await geocoder.SearchAsync("another query");
        clock.UtcNow += TimeSpan.FromMinutes(9);
        var third = await geocoder.SearchAsync("third query");

        Assert.Equal(GeocodeStatus.RateLimited, first.Status);
        Assert.Equal(GeocodeStatus.RateLimited, second.Status);
        Assert.Equal(GeocodeStatus.RateLimited, third.Status);
        Assert.Single(handler.Calls);

        clock.UtcNow += TimeSpan.FromMinutes(2);
        await geocoder.SearchAsync("fourth query");
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task Nominatim_sends_hebrew_israel_and_an_identifying_user_agent()
    {
        var handler = new Handler(_ => Json(NominatimBody));
        var geocoder = new NominatimGeocoder(new Factory(handler), new FixedClock(Now), new MemorySettings(), NullLogger<NominatimGeocoder>.Instance);

        await geocoder.SearchAsync("herzl 10");

        var query = handler.Calls[0].Uri.Query;
        Assert.Contains("countrycodes=il", query);
        Assert.Contains("accept-language=he", query);
        Assert.Contains("github.com", handler.Calls[0].Request.Headers.UserAgent.ToString());
    }

    // ---- disk cache ----

    [Fact]
    public async Task An_identical_query_is_served_from_the_disk_cache_after_a_restart()
    {
        var path = Path.Combine(_directory, "geocode.dpapi");
        var clock = new FixedClock(Now);
        var handler = new Handler(_ => Json(NominatimBody));
        var first = new NominatimGeocoder(new Factory(handler), clock, new MemorySettings(), NullLogger<NominatimGeocoder>.Instance,
            diskCache: new GeocodeDiskCache(clock, path: path));
        await first.SearchAsync("herzl 10");

        var restarted = new NominatimGeocoder(new Factory(handler), clock, new MemorySettings(), NullLogger<NominatimGeocoder>.Instance,
            diskCache: new GeocodeDiskCache(clock, path: path));
        var again = await restarted.SearchAsync("herzl 10");

        Assert.Single(handler.Calls);
        Assert.Equal(GeocodeStatus.Ok, again.Status);
        Assert.Equal("Herzl 10, Tel Aviv", again.Places[0].Label);
    }

    [Fact]
    public async Task The_disk_cache_expires_after_24_hours()
    {
        var path = Path.Combine(_directory, "geocode.dpapi");
        var clock = new FixedClock(Now);
        var handler = new Handler(_ => Json(NominatimBody));
        NominatimGeocoder Make() => new(new Factory(handler), clock, new MemorySettings(), NullLogger<NominatimGeocoder>.Instance,
            (span, _) => Task.CompletedTask, new GeocodeDiskCache(clock, path: path));
        await Make().SearchAsync("herzl 10");

        clock.UtcNow += TimeSpan.FromHours(25);
        await Make().SearchAsync("herzl 10");

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task The_disk_cache_file_holds_neither_the_query_nor_the_place_in_clear()
    {
        var path = Path.Combine(_directory, "geocode.dpapi");
        var clock = new FixedClock(Now);
        var cache = new GeocodeDiskCache(clock, path: path);
        cache.Store("https://x/|secret street 7", new GeocodeResult(GeocodeStatus.Ok, [new GeocodedPlace("Secret Street 7, Haifa", 32.8, 34.99)]));

        await Task.Yield();
        var bytes = await File.ReadAllBytesAsync(path);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain("Secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret street", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_damaged_cache_file_is_ignored()
    {
        var path = Path.Combine(_directory, "geocode.dpapi");
        File.WriteAllBytes(path, [1, 2, 3, 4]);

        var cache = new GeocodeDiskCache(new FixedClock(Now), path: path);

        Assert.False(cache.TryGet("anything", out _));
    }

    // ---- Google Places ----

    private (GooglePlacesGeocoder Geocoder, Handler Handler, MemorySettings Settings, FixedClock Clock) Google(string? key = "test-key", int cap = 200)
    {
        var handler = new Handler(request => request.Method == HttpMethod.Post ? Json(AutocompleteJson) : Json(DetailsJson));
        var settings = new MemorySettings();
        settings.Config.GooglePlacesDailySessionCap = cap;
        var clock = new FixedClock(Now);
        var geocoder = new GooglePlacesGeocoder(new Factory(handler), clock, settings, new Secrets(key),
            new PlacesUsageStore(clock, Path.Combine(_directory, "usage.json")), NullLogger<GooglePlacesGeocoder>.Instance);
        return (geocoder, handler, settings, clock);
    }

    [Fact]
    public async Task Google_is_off_without_a_key_and_sends_nothing()
    {
        var (geocoder, handler, _, _) = Google(key: null);

        var result = await geocoder.SuggestAsync("הרצל");

        Assert.Equal(GeocodeStatus.Disabled, result.Status);
        Assert.False(geocoder.SupportsAutocomplete);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Google_asks_for_hebrew_and_israel_with_the_key_in_a_header_not_the_url()
    {
        var (geocoder, handler, _, _) = Google();

        var result = await geocoder.SuggestAsync("הרצל");

        Assert.Equal(GeocodeStatus.Ok, result.Status);
        Assert.True(geocoder.SupportsAutocomplete);
        var call = handler.Calls.Single();
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.DoesNotContain("test-key", call.Uri.ToString());
        Assert.Equal("test-key", call.Request.Headers.GetValues("X-Goog-Api-Key").Single());
        Assert.Contains("\"languageCode\":\"he\"", call.Body);
        Assert.Contains("\"regionCode\":\"il\"", call.Body);
        Assert.Contains("\"sessionToken\":", call.Body);
        Assert.Equal(2, result.Places.Count);
        Assert.All(result.Places, place => Assert.True(place.IsUnresolved));
    }

    [Fact]
    public async Task One_typing_session_shares_a_token_counts_once_and_details_closes_it()
    {
        var (geocoder, handler, _, _) = Google();

        await geocoder.SuggestAsync("הרצ");
        var suggestions = await geocoder.SuggestAsync("הרצל");
        var resolved = await geocoder.ResolveAsync(suggestions.Places[0]);
        await geocoder.SuggestAsync("דיזנגוף");

        string Token(string body) => System.Text.RegularExpressions.Regex.Match(body, "\"sessionToken\":\"([^\"]+)\"").Groups[1].Value;
        var first = Token(handler.Calls[0].Body);
        Assert.Equal(first, Token(handler.Calls[1].Body));
        Assert.Contains("sessionToken=" + first, handler.Calls[2].Uri.Query);
        Assert.Equal("location", handler.Calls[2].Request.Headers.GetValues("X-Goog-FieldMask").Single());
        Assert.NotEqual(first, Token(handler.Calls[3].Body));
        Assert.Equal(32.0644, resolved!.Latitude);
        Assert.False(resolved.IsUnresolved);
    }

    [Fact]
    public async Task The_daily_session_cap_stops_all_calls_and_resets_the_next_day()
    {
        var (geocoder, handler, _, clock) = Google(cap: 2);

        for (var i = 0; i < 2; i++)
        {
            var places = (await geocoder.SuggestAsync("הרצל")).Places;
            await geocoder.ResolveAsync(places[0]);
        }

        var capped = await geocoder.SuggestAsync("הרצל");

        Assert.Equal(GeocodeStatus.DailyCapReached, capped.Status);
        Assert.Equal(4, handler.Calls.Count);

        clock.UtcNow += TimeSpan.FromDays(1);
        Assert.Equal(GeocodeStatus.Ok, (await geocoder.SuggestAsync("הרצל")).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Google_pauses_for_ten_minutes_after_429_or_403(HttpStatusCode status)
    {
        var handler = new Handler(_ => Json("{}", status));
        var clock = new FixedClock(Now);
        var geocoder = new GooglePlacesGeocoder(new Factory(handler), clock, new MemorySettings(), new Secrets("k"),
            new PlacesUsageStore(clock, Path.Combine(_directory, "usage.json")), NullLogger<GooglePlacesGeocoder>.Instance);

        var first = await geocoder.SuggestAsync("הרצל");
        var second = await geocoder.SuggestAsync("הרצל 1");

        Assert.Equal(GeocodeStatus.RateLimited, first.Status);
        Assert.Equal(GeocodeStatus.RateLimited, second.Status);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Google_is_off_when_address_search_is_switched_off()
    {
        var (geocoder, handler, settings, _) = Google();
        await geocoder.EnsureKeyAsync();
        settings.Config.AddressSearchEnabled = false;

        var result = await geocoder.SuggestAsync("הרצל");

        Assert.Equal(GeocodeStatus.Disabled, result.Status);
        Assert.False(geocoder.SupportsAutocomplete);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void Google_parsing_rejects_hostile_ids_and_oversized_text_and_limits_results()
    {
        var json = """
            {"suggestions":[
              {"placePrediction":{"placeId":"bad id/../x","text":{"text":"x"}}},
              {"placePrediction":{"placeId":"ok1","text":{"text":"%LONG%"}}},
              {"placePrediction":{"placeId":"ok2","text":{"text":"fine"}}}
            ]}
            """.Replace("%LONG%", new string('a', 500));

        var places = GooglePlacesGeocoder.ParseAutocomplete(Encoding.UTF8.GetBytes(json));

        Assert.Equal(["fine"], places.Select(p => p.Label));
        Assert.Empty(GooglePlacesGeocoder.ParseAutocomplete("{}"u8));
    }

    [Fact]
    public void Google_details_outside_israel_are_rejected()
    {
        var suggestion = new GeocodedPlace("x", 0, 0, "ok1");

        Assert.Null(GooglePlacesGeocoder.ParseDetails(suggestion, """{"location":{"latitude":48.8,"longitude":2.3}}"""u8));
        Assert.NotNull(GooglePlacesGeocoder.ParseDetails(suggestion, DetailsJson.AsSpan().ToString().Select(c => (byte)c).ToArray()));
    }

    [Fact]
    public async Task The_selector_uses_nominatim_for_an_explicit_search_when_google_is_capped_but_never_while_typing()
    {
        var google = Google(cap: 1);
        await google.Geocoder.EnsureKeyAsync();
        await google.Geocoder.SuggestAsync("הרצל");
        google.Clock.UtcNow += TimeSpan.FromMinutes(5);
        var nominatimHandler = new Handler(_ => Json(NominatimBody));
        var nominatim = new NominatimGeocoder(new Factory(nominatimHandler), google.Clock, google.Settings, NullLogger<NominatimGeocoder>.Instance);
        var selector = new GeocoderSelector(nominatim, google.Geocoder);

        var typing = await selector.SuggestAsync("הרצל 2");
        var enter = await selector.SearchAsync("herzl ten");

        Assert.Equal(GeocodeStatus.DailyCapReached, typing.Status);
        Assert.Equal(GeocodeStatus.Ok, enter.Status);
        Assert.Single(nominatimHandler.Calls);
    }

    [Fact]
    public async Task Places_usage_survives_a_restart()
    {
        var path = Path.Combine(_directory, "usage.json");
        var clock = new FixedClock(Now);
        var first = new PlacesUsageStore(clock, path);
        Assert.True(first.TryStartSession(2));
        Assert.True(first.TryStartSession(2));

        var restarted = new PlacesUsageStore(clock, path);

        Assert.Equal(2, restarted.SessionsToday);
        Assert.False(restarted.TryStartSession(2));
        Assert.Equal(2, restarted.SessionsToday);
    }

    [Fact]
    public void The_places_cap_must_be_within_range()
    {
        var config = new BusPulse.Configuration.AppConfig { GooglePlacesDailySessionCap = 0 };

        Assert.Throws<InvalidDataException>(config.Validate);
    }
}
