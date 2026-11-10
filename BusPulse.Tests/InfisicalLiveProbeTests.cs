using System.Net.Http;
using BusPulse.Services.Secrets;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace BusPulse.Tests;

/// <summary>
/// Reads the two Google secrets from the real Infisical project with the Machine Identity in <c>BUSPULSE_INFISICAL_*</c>;
/// excluded from CI. Run with <c>dotnet test --filter "FullyQualifiedName~InfisicalLiveProbeTests"</c>. Prints lengths and
/// the provider's log lines only, never a secret value.
/// </summary>
[Trait("Category", "Live")]
public sealed class InfisicalLiveProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Infisical_ReturnsBothGoogleSecrets()
    {
        var options = InfisicalOptions.FromEnvironment();
        Assert.True(
            options.IsConfigured,
            "Set BUSPULSE_INFISICAL_CLIENT_ID, BUSPULSE_INFISICAL_CLIENT_SECRET and BUSPULSE_INFISICAL_PROJECT_ID (see docs/SECRETS_SETUP.md), then open a new terminal.");
        output.WriteLine($"host {options.BaseUri.Host}, environment {options.Environment}, path {options.Path}");
        var provider = new InfisicalSecretProvider(new RealFactory(), options, new OutputLogger(output));

        var clientId = await provider.GetAsync(SecretNames.GoogleClientId, default);
        var clientSecret = await provider.GetAsync(SecretNames.GoogleClientSecret, default);

        output.WriteLine($"{SecretNames.GoogleClientId}: {(clientId is null ? "missing" : $"{clientId.Length} characters")}");
        output.WriteLine($"{SecretNames.GoogleClientSecret}: {(clientSecret is null ? "missing" : $"{clientSecret.Length} characters")}");
        Assert.NotNull(clientId);
        Assert.NotNull(clientSecret);
        Assert.EndsWith(".apps.googleusercontent.com", clientId, StringComparison.Ordinal);   // the shape of a Google OAuth client id
    }

    private sealed class RealFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    private sealed class OutputLogger(ITestOutputHelper output) : ILogger<InfisicalSecretProvider>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            output.WriteLine($"{logLevel}: {formatter(state, exception)}");
    }
}
