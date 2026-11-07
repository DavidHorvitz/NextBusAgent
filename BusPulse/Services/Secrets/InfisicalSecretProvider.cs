using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Secrets;

/// <summary>
/// Reads secrets from Infisical with a Machine Identity (Universal Auth). Hardened like the other providers: HTTPS only,
/// fixed host, no redirects (set on the named client), response cap, strict JSON, in-memory caches. A secret value, access
/// token or client secret is never logged; only the secret name and a status are.
/// </summary>
public sealed class InfisicalSecretProvider(
    IHttpClientFactory httpClientFactory,
    InfisicalOptions options,
    ILogger<InfisicalSecretProvider> logger,
    TimeProvider? timeProvider = null) : ISecretProvider
{
    public const string ClientName = "Infisical";
    public const int MaxResponseBytes = 64 * 1024;
    public static readonly TimeSpan ValueCacheDuration = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FailureCacheDuration = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TokenSafetyMargin = TimeSpan.FromSeconds(60);

    private static readonly JsonDocumentOptions StrictOptions = new()
    {
        MaxDepth = 16,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (string? Value, DateTimeOffset Expires)> _cache = [];
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public bool IsConfigured => options.IsConfigured;

    public string Describe => IsConfigured ? "Infisical" : "Infisical (not configured)";

    public async Task<string?> GetAsync(string name, CancellationToken ct)
    {
        if (!IsConfigured || !SecretNames.IsValid(name))
        {
            return null;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (_cache.TryGetValue(name, out var cached) && cached.Expires > now)
            {
                return cached.Value;
            }

            var value = await FetchAsync(name, ct);
            _cache[name] = (value, now + (value is null ? FailureCacheDuration : ValueCacheDuration));
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> FetchAsync(string name, CancellationToken ct)
    {
        try
        {
            var token = await GetTokenAsync(ct);
            if (token is null)
            {
                return null;
            }

            var query = $"projectId={Uri.EscapeDataString(options.ProjectId!)}&environment={Uri.EscapeDataString(options.Environment)}" +
                $"&secretPath={Uri.EscapeDataString(options.Path)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(options.BaseUri, $"/api/v4/secrets/{name}?{query}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var client = httpClientFactory.CreateClient(ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    _token = null;
                }

                logger.LogWarning("Infisical secret {Name} not read: HTTP {Status}", name, (int)response.StatusCode);
                return null;
            }

            var body = await ReadCappedAsync(response, ct);
            if (body is null)
            {
                logger.LogWarning("Infisical secret {Name} not read: response too large", name);
                return null;
            }

            var value = ParseSecret(body);
            if (value is null)
            {
                logger.LogWarning("Infisical secret {Name} not read: unexpected response", name);
            }

            return value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogWarning("Infisical secret {Name} not read: {Error}", name, exception.GetType().Name);
            return null;
        }
    }

    private async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && _tokenExpires > _time.GetUtcNow())
        {
            return _token;
        }

        _token = null;
        var payload = JsonSerializer.Serialize(new { clientId = options.ClientId, clientSecret = options.ClientSecret });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(options.BaseUri, "/api/v1/auth/universal-auth/login"))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        var client = httpClientFactory.CreateClient(ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            logger.LogWarning("Infisical login failed: HTTP {Status}", (int)response.StatusCode);
            return null;
        }

        var body = await ReadCappedAsync(response, ct);
        if (body is null)
        {
            logger.LogWarning("Infisical login failed: response too large");
            return null;
        }

        using var document = JsonDocument.Parse(body, StrictOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("accessToken", out var accessToken) || accessToken.ValueKind != JsonValueKind.String ||
            accessToken.GetString() is not { Length: > 0 and <= 8192 } tokenText ||
            !root.TryGetProperty("expiresIn", out var expiresIn) || expiresIn.ValueKind != JsonValueKind.Number ||
            !expiresIn.TryGetInt64(out var seconds) || seconds <= 0)
        {
            logger.LogWarning("Infisical login failed: unexpected response");
            return null;
        }

        _token = tokenText;
        _tokenExpires = _time.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(seconds, 86_400)) - TokenSafetyMargin;
        return _token;
    }

    private static string? ParseSecret(byte[] body)
    {
        using var document = JsonDocument.Parse(body, StrictOptions);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("secret", out var secret) && secret.ValueKind == JsonValueKind.Object &&
            secret.TryGetProperty("secretValue", out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: > 0 and <= 8192 } text)
        {
            return text;
        }

        return null;
    }

    private static async Task<byte[]?> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
