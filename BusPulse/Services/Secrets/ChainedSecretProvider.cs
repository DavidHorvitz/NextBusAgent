namespace BusPulse.Services.Secrets;

/// <summary>Asks Infisical first when it is configured, then falls back to the environment. A failure in the first never reaches callers.</summary>
public sealed class ChainedSecretProvider(InfisicalSecretProvider infisical, EnvironmentSecretProvider environment) : ISecretProvider
{
    public string Describe => infisical.IsConfigured ? "Infisical (environment variables as fallback)" : environment.Describe;

    public async Task<string?> GetAsync(string name, CancellationToken ct)
    {
        if (infisical.IsConfigured)
        {
            try
            {
                var value = await infisical.GetAsync(name, ct);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Fall back to the environment below.
            }
        }

        return await environment.GetAsync(name, ct);
    }
}
