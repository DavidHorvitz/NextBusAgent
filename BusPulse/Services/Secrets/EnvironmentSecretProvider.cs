namespace BusPulse.Services.Secrets;

/// <summary>Reads a secret from the environment variable <c>BUSPULSE_&lt;NAME&gt;</c>.</summary>
public sealed class EnvironmentSecretProvider : ISecretProvider
{
    public const string Prefix = "BUSPULSE_";

    public string Describe => "environment variables";

    public Task<string?> GetAsync(string name, CancellationToken ct)
    {
        if (!SecretNames.IsValid(name))
        {
            return Task.FromResult<string?>(null);
        }

        var value = Environment.GetEnvironmentVariable(Prefix + name);
        return Task.FromResult<string?>(string.IsNullOrWhiteSpace(value) ? null : value);
    }
}
