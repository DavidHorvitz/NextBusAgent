using System.Text.RegularExpressions;

namespace BusPulse.Services.Secrets;

/// <summary>Reads named secrets. Implementations never throw for a missing or unreachable secret; they return null.</summary>
public interface ISecretProvider
{
    /// <summary>Returns the secret value, or null when it is not available. Only the caller's own cancellation is rethrown.</summary>
    Task<string?> GetAsync(string name, CancellationToken ct);

    /// <summary>A short, secret-free description of where values come from (for logs and diagnostics).</summary>
    string Describe { get; }
}

public static partial class SecretNames
{
    public const string GoogleClientId = "GOOGLE_CLIENT_ID";
    public const string GoogleClientSecret = "GOOGLE_CLIENT_SECRET";
    public const string GooglePlacesApiKey = "GOOGLE_PLACES_API_KEY";

    public static bool IsValid(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Z0-9_]{1,64}$")]
    private static partial Regex NamePattern();
}
