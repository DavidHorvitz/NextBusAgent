using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BusPulse.Services.Calendar;

/// <summary>A timed calendar event; all-day events are not offered because they have no departure time.</summary>
public sealed record CalendarEvent(string Id, string Title, DateTimeOffset Start, string? Location);

public sealed record PkcePair(string Verifier, string Challenge);

public sealed record TokenResult(string? AccessToken, string? RefreshToken, int ExpiresInSeconds, string? Error)
{
    public bool IsSuccess => !string.IsNullOrEmpty(AccessToken) && Error is null;
}

/// <summary>Pure helpers for the Google OAuth 2.0 installed-app flow (PKCE, loopback redirect) and Calendar API responses.</summary>
public static class GoogleOAuth
{
    /// <summary>Read-only access to events; nothing can be created, changed or deleted.</summary>
    public const string CalendarScope = "https://www.googleapis.com/auth/calendar.events.readonly";

    public const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string EventsEndpoint = "https://www.googleapis.com/calendar/v3/calendars/primary/events";

    private const int MaxEvents = 50;
    private const int MaxTitleLength = 128;
    private const int MaxLocationLength = 256;

    private static readonly JsonDocumentOptions StrictOptions = new()
    {
        MaxDepth = 16,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    public static PkcePair CreatePkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return new PkcePair(verifier, ChallengeFor(verifier));
    }

    public static string ChallengeFor(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string CreateState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static Uri BuildAuthUrl(string clientId, string redirectUri, string challenge, string state)
    {
        var query = string.Join(
            '&',
            $"client_id={Uri.EscapeDataString(clientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            "response_type=code",
            $"scope={Uri.EscapeDataString(CalendarScope)}",
            $"code_challenge={Uri.EscapeDataString(challenge)}",
            "code_challenge_method=S256",
            $"state={Uri.EscapeDataString(state)}",
            "access_type=offline",
            "prompt=consent");
        return new Uri($"{AuthEndpoint}?{query}", UriKind.Absolute);
    }

    public static TokenResult ParseTokenResponse(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), StrictOptions);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new TokenResult(null, null, 0, "invalid_response");
        }

        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            return new TokenResult(null, null, 0, Truncate(error.GetString(), 64) ?? "error");
        }

        var access = Text(root, "access_token", 4096);
        if (string.IsNullOrEmpty(access))
        {
            return new TokenResult(null, null, 0, "missing_access_token");
        }

        var expires = root.TryGetProperty("expires_in", out var expiresElement) &&
            expiresElement.ValueKind == JsonValueKind.Number &&
            expiresElement.TryGetInt32(out var seconds)
                ? Math.Clamp(seconds, 60, 86_400)
                : 3600;
        return new TokenResult(access, Text(root, "refresh_token", 4096), expires, null);
    }

    public static IReadOnlyList<CalendarEvent> ParseEvents(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray(), StrictOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var events = new List<CalendarEvent>();
        foreach (var item in items.EnumerateArray().Take(MaxEvents))
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("start", out var start) || start.ValueKind != JsonValueKind.Object ||
                !start.TryGetProperty("dateTime", out var dateTime) || dateTime.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(dateTime.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var begins))
            {
                continue;
            }

            var id = Text(item, "id", 256);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            events.Add(new CalendarEvent(id, Text(item, "summary", MaxTitleLength) ?? string.Empty, begins, Text(item, "location", MaxLocationLength)));
        }

        return events.OrderBy(calendarEvent => calendarEvent.Start).ToArray();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? Text(JsonElement element, string name, int maxLength) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? Truncate(property.GetString(), maxLength)
            : null;

    private static string? Truncate(string? text, int maxLength) =>
        text is null ? null : text.Length <= maxLength ? text : text[..maxLength];
}
