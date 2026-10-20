namespace BusPulse.Services.LiveData;

public interface IScheduleProvider
{
    Task<ProviderResult> GetScheduleAsync(string stopCode, CancellationToken cancellationToken = default);
}
