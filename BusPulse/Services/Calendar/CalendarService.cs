using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BusPulse.Configuration;
using BusPulse.Services.Secrets;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Calendar;

public enum CalendarConnectStatus
{
    Connected,
    NotConfigured,
    Cancelled,
    StateMismatch,
    Failed
}

public sealed record CalendarConnectResult(CalendarConnectStatus Status, string? Detail = null);

/// <summary>Connects a read-only Google Calendar. The UI uses this interface so the view model can be tested without Google.</summary>
public interface ICalendarService
{
    /// <summary>Whether a Google client id and secret were found (last resolved value; see <see cref="RefreshConfigurationAsync"/>).</summary>
    bool IsConfigured { get; }

    /// <summary>Looks the Google client id and secret up again (Infisical, then environment) and returns whether both exist.</summary>
    Task<bool> RefreshConfigurationAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsConfigured);

    bool IsConnected { get; }

    Task<CalendarConnectResult> ConnectAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CalendarEvent>> GetUpcomingAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    void Disconnect();
}

/// <summary>
/// Google OAuth "installed app" flow: PKCE, the system browser, and a one-shot listener on 127.0.0.1 for the redirect.
/// The client id and secret come from environment variables (or the config), never from the repository. The refresh token is
/// stored with DPAPI for the current Windows user only.
/// </summary>
public sealed class GoogleCalendarService(
    AppConfig config,
    ISecretProvider secrets,
    IHttpClientFactory httpClientFactory,
    CalendarTokenStore tokens,
    ILogger<GoogleCalendarService> logger) : ICalendarService
{
    public const string ClientName = "Google";
    private static readonly TimeSpan ConsentTimeout = TimeSpan.FromMinutes(3);

    private string? _clientId;
    private string? _clientSecret;
    private bool _resolved;

    /// <summary>
    /// Secrets come from <see cref="ISecretProvider"/> (Infisical, then <c>BUSPULSE_GOOGLE_CLIENT_ID</c> / <c>BUSPULSE_GOOGLE_CLIENT_SECRET</c>);
    /// the config is the last resort. Before the first lookup this answers from the environment and config only.
    /// </summary>
    public bool IsConfigured => _resolved
        ? !string.IsNullOrWhiteSpace(_clientId) && !string.IsNullOrWhiteSpace(_clientSecret)
        : !string.IsNullOrWhiteSpace(EnvironmentOrConfig(SecretNames.GoogleClientId, config.GoogleClientId)) &&
          !string.IsNullOrWhiteSpace(EnvironmentOrConfig(SecretNames.GoogleClientSecret, config.GoogleClientSecret));

    private static string? EnvironmentOrConfig(string name, string? fromConfig) =>
        Environment.GetEnvironmentVariable(EnvironmentSecretProvider.Prefix + name) is { Length: > 0 } fromEnv ? fromEnv : fromConfig;

    public async Task<bool> RefreshConfigurationAsync(CancellationToken cancellationToken = default)
    {
        _clientId = await secrets.GetAsync(SecretNames.GoogleClientId, cancellationToken) ?? config.GoogleClientId;
        _clientSecret = await secrets.GetAsync(SecretNames.GoogleClientSecret, cancellationToken) ?? config.GoogleClientSecret;
        _resolved = true;
        return IsConfigured;
    }

    private string? ClientId => _clientId;

    private string? ClientSecret => _clientSecret;

    public bool IsConnected => tokens.Exists;

    public async Task<CalendarConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (!await RefreshConfigurationAsync(cancellationToken))
        {
            return new CalendarConnectResult(CalendarConnectStatus.NotConfigured);
        }

        var port = FreeLoopbackPort();
        var redirect = $"http://127.0.0.1:{port}/callback/";
        var pkce = GoogleOAuth.CreatePkce();
        var state = GoogleOAuth.CreateState();
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect);
        try
        {
            listener.Start();
            Process.Start(new ProcessStartInfo(GoogleOAuth.BuildAuthUrl(ClientId!, redirect.TrimEnd('/'), pkce.Challenge, state).ToString())
            {
                UseShellExecute = true
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConsentTimeout);
            var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
            var query = context.Request.QueryString;
            await RespondAsync(context, "BusPulse: you can close this window and return to the app.");

            if (query["error"] is not null)
            {
                return new CalendarConnectResult(CalendarConnectStatus.Cancelled, query["error"]);
            }

            if (query["state"] != state)
            {
                logger.LogWarning("Calendar redirect state did not match; the response was ignored.");
                return new CalendarConnectResult(CalendarConnectStatus.StateMismatch);
            }

            var code = query["code"];
            if (string.IsNullOrEmpty(code))
            {
                return new CalendarConnectResult(CalendarConnectStatus.Failed, "missing_code");
            }

            var token = await ExchangeAsync(
                new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["code_verifier"] = pkce.Verifier,
                    ["grant_type"] = "authorization_code",
                    ["redirect_uri"] = redirect.TrimEnd('/')
                },
                cancellationToken);
            if (!token.IsSuccess || string.IsNullOrEmpty(token.RefreshToken))
            {
                return new CalendarConnectResult(CalendarConnectStatus.Failed, token.Error ?? "no_refresh_token");
            }

            tokens.Save(token.RefreshToken);
            return new CalendarConnectResult(CalendarConnectStatus.Connected);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CalendarConnectResult(CalendarConnectStatus.Cancelled, "timeout");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException or System.Text.Json.JsonException or CryptographicException)
        {
            logger.LogWarning(exception, "Calendar connection failed.");
            return new CalendarConnectResult(CalendarConnectStatus.Failed, exception.GetType().Name);
        }
        finally
        {
            if (listener.IsListening)
            {
                listener.Stop();
            }
        }
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetUpcomingAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var refresh = tokens.Load();
        if (refresh is null)
        {
            return [];
        }

        var token = await ExchangeAsync(
            new Dictionary<string, string> { ["refresh_token"] = refresh, ["grant_type"] = "refresh_token" },
            cancellationToken);
        if (!token.IsSuccess)
        {
            logger.LogWarning("Calendar token refresh failed: {Error}", token.Error);
            return [];
        }

        var client = httpClientFactory.CreateClient(ClientName);
        var url = $"{GoogleOAuth.EventsEndpoint}?singleEvents=true&orderBy=startTime&maxResults=50" +
            $"&timeMin={Uri.EscapeDataString(now.UtcDateTime.ToString("o"))}" +
            $"&timeMax={Uri.EscapeDataString(now.AddHours(48).UtcDateTime.ToString("o"))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Calendar events request returned {Status}.", (int)response.StatusCode);
            return [];
        }

        return GoogleOAuth.ParseEvents(await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    public void Disconnect() => tokens.Delete();

    private async Task<TokenResult> ExchangeAsync(Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        fields["client_id"] = ClientId!;
        fields["client_secret"] = ClientSecret!;
        using var content = new FormUrlEncodedContent(fields);
        using var response = await client.PostAsync(GoogleOAuth.TokenEndpoint, content, cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return body.Length > 16 * 1024 ? new TokenResult(null, null, 0, "response_too_large") : GoogleOAuth.ParseTokenResponse(body);
    }

    private static async Task RespondAsync(HttpListenerContext context, string message)
    {
        var bytes = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>BusPulse</title><p style=\"font-family:Segoe UI;font-size:18px\">{message}</p>");
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}

/// <summary>The refresh token, encrypted for the current Windows user (DPAPI) in %LOCALAPPDATA%\BusPulse\calendar.dpapi.</summary>
public sealed class CalendarTokenStore
{
    private static readonly byte[] Entropy = "BusPulse.Calendar.v1"u8.ToArray();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusPulse", "calendar.dpapi");

    public bool Exists => File.Exists(_path);

    public void Save(string refreshToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(refreshToken), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, protectedBytes);
    }

    public string? Load()
    {
        if (!Exists)
        {
            return null;
        }

        var bytes = File.ReadAllBytes(_path);
        return bytes.Length is 0 or > 8192
            ? null
            : Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }

    public void Delete()
    {
        if (Exists)
        {
            File.Delete(_path);
        }
    }
}
