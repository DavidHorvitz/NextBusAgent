using System.Text.RegularExpressions;

namespace BusPulse.Services.Secrets;

/// <summary>Connection details for Infisical (Machine Identity, Universal Auth). Read from environment variables only.</summary>
public sealed partial record InfisicalOptions(
    string? ClientId,
    string? ClientSecret,
    string? ProjectId,
    string Environment,
    string Path,
    Uri BaseUri)
{
    public const string DefaultUrl = "https://app.infisical.com";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(ProjectId);

    public static InfisicalOptions FromEnvironment() => From(name => System.Environment.GetEnvironmentVariable("BUSPULSE_INFISICAL_" + name));

    /// <summary>Builds options from a lookup of the <c>BUSPULSE_INFISICAL_*</c> suffixes. Invalid values make the result unconfigured.</summary>
    public static InfisicalOptions From(Func<string, string?> get)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        var clientId = Clean(get("CLIENT_ID"));
        var clientSecret = Clean(get("CLIENT_SECRET"));
        var projectId = Clean(get("PROJECT_ID"));
        var environment = Clean(get("ENV")) ?? "dev";
        var path = Clean(get("PATH")) ?? "/";
        var url = Clean(get("URL")) ?? DefaultUrl;

        var valid = Uri.TryCreate(url, UriKind.Absolute, out var baseUri) &&
            baseUri.Scheme == Uri.UriSchemeHttps &&
            string.IsNullOrEmpty(baseUri.UserInfo) &&
            string.IsNullOrEmpty(baseUri.Query) &&
            string.IsNullOrEmpty(baseUri.Fragment) &&
            (projectId is null || IdPattern().IsMatch(projectId)) &&
            EnvPattern().IsMatch(environment) &&
            PathPattern().IsMatch(path);

        if (!valid)
        {
            return new InfisicalOptions(null, null, null, "dev", "/", new Uri(DefaultUrl));
        }

        return new InfisicalOptions(clientId, clientSecret, projectId, environment, path, baseUri!);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex EnvPattern();

    [GeneratedRegex(@"^/[A-Za-z0-9_./-]{0,255}$")]
    private static partial Regex PathPattern();
}
