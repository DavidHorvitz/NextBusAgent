using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.LiveData;

/// <summary>
/// Israel Railways departures. Disabled until an official API key is configured as <c>RailApiKey</c>
/// (user-secrets or environment); the unofficial key scraped from rail.co.il is deliberately not used.
/// </summary>
public sealed class RailProvider(IConfiguration configuration, ILogger<RailProvider> logger) : ILiveArrivalsProvider
{
    public ArrivalSource Source => ArrivalSource.Rail;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(configuration["RailApiKey"]);

    public Task<ProviderResult> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            logger.LogDebug("Rail provider skipped: RailApiKey is not configured.");
            return Task.FromResult(ProviderResult.Failed(ProviderStatus.Disabled, "Rail API key is not configured."));
        }

        return Task.FromResult(ProviderResult.Failed(ProviderStatus.Unavailable, "Rail API client is not implemented yet."));
    }
}
