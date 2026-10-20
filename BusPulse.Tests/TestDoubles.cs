using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;

namespace BusPulse.Tests;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

internal sealed class ImmediateDispatcher : IDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeLocation : ILocationService
{
    public int Calls { get; private set; }
    public LocationResult Result { get; set; } = LocationResult.Failed(LocationStatus.Unavailable);

    public Task<LocationResult> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeCatalog(params Stop[] stops) : IStopCatalog
{
    public Exception? Throw { get; set; }

    public Task<IReadOnlyList<Stop>> GetStopsAsync(CancellationToken cancellationToken = default) =>
        Throw is null ? Task.FromResult<IReadOnlyList<Stop>>(stops) : Task.FromException<IReadOnlyList<Stop>>(Throw);
}
