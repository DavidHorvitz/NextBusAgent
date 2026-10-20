namespace BusPulse.Services.LiveData;

public interface ILiveArrivalsProvider
{
    ArrivalSource Source { get; }

    Task<ProviderResult> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default);
}
