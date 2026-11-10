using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.Calendar;
using BusPulse.Services.Secrets;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BusPulse.Tests;

public sealed class SecretsTests
{
    // These response shapes come from the official Infisical API reference (Universal Auth login and "Retrieve secret"),
    // not from a recorded response (a real one contains the secrets). InfisicalLiveProbeTests checks the live service;
    // see docs/live-data-probe.md.
    private const string LoginJson = """{"accessToken":"tok-1","expiresIn":7200,"accessTokenMaxTTL":2592000,"tokenType":"Bearer"}""";
    private const string SecretJson = """{"secret":{"id":"1","_id":"1","workspace":"w","environment":"dev","version":1,"type":"shared","secretKey":"GOOGLE_CLIENT_ID","secretValue":"value-abc","secretComment":"","createdAt":"2025-01-01T00:00:00Z","updatedAt":"2025-01-01T00:00:00Z","secretValueHidden":false,"secretPath":"/"}}""";

    private static InfisicalOptions Configured() => InfisicalOptions.From(name => name switch
    {
        "CLIENT_ID" => "client-id-x",
        "CLIENT_SECRET" => "client-secret-x",
        "PROJECT_ID" => "project-x",
        _ => null
    });

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception) + exception?.ToString());
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Route(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.Contains("universal-auth", StringComparison.Ordinal) ? Json(LoginJson) : Json(SecretJson);

    private static (InfisicalSecretProvider Provider, Handler Handler, CapturingLogger<InfisicalSecretProvider> Log, Clock Clock) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Handler(respond);
        var log = new CapturingLogger<InfisicalSecretProvider>();
        var clock = new Clock();
        return (new InfisicalSecretProvider(new Factory(handler), Configured(), log, clock), handler, log, clock);
    }

    [Fact]
    public async Task LoginAndRead_Succeeds()
    {
        var (provider, handler, _, _) = Make(Route);

        var value = await provider.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None);

        Assert.Equal("value-abc", value);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("POST /api/v1/auth/universal-auth/login", handler.Requests[0]);
        Assert.StartsWith("GET /api/v4/secrets/GOOGLE_CLIENT_ID?projectId=project-x&environment=dev&secretPath=%2F", handler.Requests[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReadErrors_ReturnNull(HttpStatusCode status)
    {
        var (provider, _, _, _) = Make(request => request.RequestUri!.AbsolutePath.Contains("universal-auth", StringComparison.Ordinal) ? Json(LoginJson) : Json("{}", status));

        Assert.Null(await provider.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task LoginErrors_ReturnNull(HttpStatusCode status)
    {
        var (provider, handler, _, _) = Make(_ => Json("{}", status));

        Assert.Null(await provider.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Timeout_IsAFailure_ButCallerCancellationIsRethrown()
    {
        var (provider, _, _, _) = Make(_ => throw new TaskCanceledException("timeout"));
        Assert.Null(await provider.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (second, _, _, _) = Make(Route);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.GetAsync("GOOGLE_CLIENT_ID", cts.Token));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"secret":{"secretValue":5}}""")]
    public async Task MalformedJson_ReturnsNull(string body)
    {
        var (provider, _, _, _) = Make(request => request.RequestUri!.AbsolutePath.Contains("universal-auth", StringComparison.Ordinal) ? Json(LoginJson) : Json(body));

        Assert.Null(await provider.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None));
    }

    [Fact]
    public async Task OversizedResponse_ReturnsNull()
    {
        var huge = "{\"secret\":{\"secretValue\":\"" + new string('a', 70 * 1024) + "\"}}";
        var (provider, _, _, _) = Make(request => request.RequestUri!.AbsolutePath.Contains("universal-auth", StringComparison.Ordinal) ? Json(LoginJson) : Json(huge));

        Assert.Null(await provider.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None));
    }

    [Fact]
    public async Task ExpiredToken_IsRefreshed_AndTokenIsReusedBeforeExpiry()
    {
        var (provider, handler, _, clock) = Make(Route);
        await provider.GetAsync("A_ONE", CancellationToken.None);
        clock.Now += TimeSpan.FromMinutes(5);
        await provider.GetAsync("A_TWO", CancellationToken.None);
        Assert.Equal(1, handler.Requests.Count(r => r.StartsWith("POST", StringComparison.Ordinal)));

        clock.Now += TimeSpan.FromHours(3);
        await provider.GetAsync("A_THREE", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count(r => r.StartsWith("POST", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CacheHit_MakesNoSecondCall_UntilTenMinutesPass()
    {
        var (provider, handler, _, clock) = Make(Route);
        await provider.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None);
        var before = handler.Requests.Count;

        await provider.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None);
        Assert.Equal(before, handler.Requests.Count);

        clock.Now += TimeSpan.FromMinutes(11);
        await provider.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None);
        Assert.Equal(before + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task InvalidName_NeverCallsTheNetwork()
    {
        var (provider, handler, _, _) = Make(Route);

        Assert.Null(await provider.GetAsync("../etc/passwd", CancellationToken.None));
        Assert.Null(await provider.GetAsync("lower", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Options_RequireHttpsAndAllThreeIds()
    {
        Assert.False(InfisicalOptions.From(_ => null).IsConfigured);
        var http = InfisicalOptions.From(name => name switch { "CLIENT_ID" => "a", "CLIENT_SECRET" => "b", "PROJECT_ID" => "c", "URL" => "http://example.com", _ => null });
        Assert.False(http.IsConfigured);
        var custom = InfisicalOptions.From(name => name switch { "CLIENT_ID" => "a", "CLIENT_SECRET" => "b", "PROJECT_ID" => "c", "URL" => "https://eu.infisical.com", "ENV" => "prod", _ => null });
        Assert.True(custom.IsConfigured);
        Assert.Equal("prod", custom.Environment);
        Assert.Equal("/", custom.Path);
    }

    [Fact]
    public async Task Chain_FallsBackToEnvironment_WhenInfisicalFails_AndNeverThrows()
    {
        var (infisical, _, _, _) = Make(_ => Json("{}", HttpStatusCode.InternalServerError));
        var chain = new ChainedSecretProvider(infisical, new EnvironmentSecretProvider());
        Environment.SetEnvironmentVariable("BUSPULSE_TEST_FALLBACK", "from-env");
        try
        {
            Assert.Equal("from-env", await chain.GetAsync("TEST_FALLBACK", CancellationToken.None));
            Assert.Null(await chain.GetAsync("TEST_MISSING_ONE", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BUSPULSE_TEST_FALLBACK", null);
        }
    }

    [Fact]
    public async Task Chain_PrefersInfisical_WhenItHasTheSecret()
    {
        var (infisical, _, _, _) = Make(Route);
        var chain = new ChainedSecretProvider(infisical, new EnvironmentSecretProvider());
        Environment.SetEnvironmentVariable("BUSPULSE_GOOGLE_CLIENT_ID", "from-env");
        try
        {
            Assert.Equal("value-abc", await chain.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BUSPULSE_GOOGLE_CLIENT_ID", null);
        }
    }

    [Fact]
    public async Task Chain_WhenInfisicalUnconfigured_UsesOnlyEnvironment()
    {
        var handler = new Handler(Route);
        var infisical = new InfisicalSecretProvider(new Factory(handler), InfisicalOptions.From(_ => null), new CapturingLogger<InfisicalSecretProvider>());
        var chain = new ChainedSecretProvider(infisical, new EnvironmentSecretProvider());

        Assert.Null(await chain.GetAsync("TEST_MISSING_TWO", CancellationToken.None));
        Assert.Empty(handler.Requests);
        Assert.Equal("environment variables", chain.Describe);
    }

    [Fact]
    public async Task Logs_NeverContainSecretValuesTokensOrClientSecret()
    {
        var statuses = new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.InternalServerError };
        foreach (var status in statuses)
        {
            var (provider, _, log, _) = Make(request => request.RequestUri!.AbsolutePath.Contains("universal-auth", StringComparison.Ordinal)
                ? Json(LoginJson)
                : status == HttpStatusCode.OK ? Json(SecretJson) : Json("""{"message":"value-abc tok-1 client-secret-x"}""", status));
            await provider.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None);

            var all = string.Join('\n', log.Messages);
            Assert.DoesNotContain("value-abc", all, StringComparison.Ordinal);
            Assert.DoesNotContain("tok-1", all, StringComparison.Ordinal);
            Assert.DoesNotContain("client-secret-x", all, StringComparison.Ordinal);
        }

        var (failing, _, failLog, _) = Make(_ => throw new HttpRequestException("boom client-secret-x tok-1"));
        await failing.GetAsync("GOOGLE_CLIENT_ID", CancellationToken.None);
        var failures = string.Join('\n', failLog.Messages);
        Assert.NotEmpty(failLog.Messages);
        Assert.DoesNotContain("client-secret-x", failures, StringComparison.Ordinal);
        Assert.DoesNotContain("tok-1", failures, StringComparison.Ordinal);
    }

    private sealed class ResolvingCalendar : ICalendarService
    {
        public bool Found { get; set; }
        public bool IsConfigured { get; private set; }
        public bool IsConnected => false;
        public int Connects { get; private set; }

        public Task<bool> RefreshConfigurationAsync(CancellationToken cancellationToken = default)
        {
            IsConfigured = Found;
            return Task.FromResult(IsConfigured);
        }

        public Task<CalendarConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Connects++;
            return Task.FromResult(new CalendarConnectResult(CalendarConnectStatus.Connected));
        }

        public Task<IReadOnlyList<CalendarEvent>> GetUpcomingAsync(DateTimeOffset now, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CalendarEvent>>([]);

        public void Disconnect()
        {
        }
    }

    private static SettingsViewModel NewSettings(ICalendarService calendar)
    {
        var settings = new MemorySettings();
        return new SettingsViewModel(
            settings, new SavedPlaceCollection(settings), new LanguageAwareLocalizer(), [], new ShellAndSettingsTests.FakeSchedule(),
            new FakeHistory(), new RecordingNotifier(), calendar);
    }

    [Fact]
    public async Task Settings_SecretsMissing_ShowsExplanationAndDoesNotConnect()
    {
        var calendar = new ResolvingCalendar { Found = false };
        var vm = NewSettings(calendar);

        await vm.ConnectCalendarCommand.ExecuteAsync(null);

        Assert.False(vm.IsCalendarConfigured);
        Assert.Equal("CalendarNotConfigured:en", vm.CalendarStatus);
        Assert.Equal(0, calendar.Connects);
    }

    [Fact]
    public async Task Settings_SecretsFound_BecomesAvailableAndConnects()
    {
        var calendar = new ResolvingCalendar { Found = true };
        var vm = NewSettings(calendar);

        await vm.RefreshCalendarConfigurationAsync();
        Assert.True(vm.IsCalendarConfigured);
        Assert.Equal("CalendarReady:en", vm.CalendarStatus);

        await vm.ConnectCalendarCommand.ExecuteAsync(null);
        Assert.Equal(1, calendar.Connects);
        Assert.True(vm.IsCalendarConnected);
    }

    [Fact]
    public void Strings_ExplainMissingGoogleSecretsInBothLanguages()
    {
        var root = AppContext.BaseDirectory;
        foreach (var language in new[] { "en", "he" })
        {
            var path = Path.Combine(root, "..", "..", "..", "..", "BusPulse", "Resources", $"Strings.{language}.xaml");
            var text = File.ReadAllText(path);
            Assert.Contains("Infisical", text, StringComparison.Ordinal);
        }
    }
}
