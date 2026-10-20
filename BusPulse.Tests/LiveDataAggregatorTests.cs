using BusPulse.Services;
using BusPulse.Services.LiveData;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class LiveDataAggregatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T19:26:00+03:00");

    [Fact]
    public async Task FreshLiveData_IsReturnedAsLive_WithSourceAndObservedAt()
    {
        var curlbus = new FakeProvider(ArrivalSource.Curlbus, Live(ArrivalSource.Curlbus, "4", minutes: 10, observedAgo: 30));
        var aggregator = Create([curlbus]);

        var result = await aggregator.GetArrivalsAsync("3490");

        var arrival = Assert.Single(result.Arrivals);
        Assert.True(arrival.IsLive);
        Assert.Equal(ArrivalSource.Curlbus, arrival.Source);
        Assert.Equal(Now.AddSeconds(-30), arrival.ObservedAt);
        Assert.Equal(ProviderStatus.Ok, result.ProviderStatuses[ArrivalSource.Curlbus]);
    }

    [Fact]
    public async Task StaleLiveData_IsDropped_AndScheduleIsUsedInstead()
    {
        var stale = new FakeProvider(ArrivalSource.Curlbus, Live(ArrivalSource.Curlbus, "4", minutes: 10, observedAgo: 600));
        var schedule = new FakeSchedule(Scheduled("4", minutes: 12));
        var aggregator = Create([stale], schedule);

        var result = await aggregator.GetArrivalsAsync("3490");

        var arrival = Assert.Single(result.Arrivals);
        Assert.False(arrival.IsLive);
        Assert.Equal(ArrivalSource.Schedule, arrival.Source);
    }

    [Theory]
    [InlineData(-30 * 24 * 60)]
    [InlineData(-10)]
    [InlineData(60 * 24 * 365 * 12)]
    public async Task LiveArrivalWithImpossibleEta_IsRejected(int minutes)
    {
        var provider = new FakeProvider(ArrivalSource.Curlbus, Live(ArrivalSource.Curlbus, "4", minutes, observedAgo: 10));
        var aggregator = Create([provider]);

        var result = await aggregator.GetArrivalsAsync("3490");

        Assert.Empty(result.Arrivals);
    }

    [Fact]
    public async Task FutureObservedAt_IsRejected()
    {
        var provider = new FakeProvider(ArrivalSource.Curlbus, Live(ArrivalSource.Curlbus, "4", minutes: 10, observedAgo: -3600));
        var aggregator = Create([provider]);

        Assert.Empty((await aggregator.GetArrivalsAsync("3490")).Arrivals);
    }

    [Fact]
    public async Task UnavailableProvider_FallsBackToSchedule_AndReportsStatus()
    {
        var down = new FakeProvider(ArrivalSource.Curlbus) { Status = ProviderStatus.Unavailable };
        var schedule = new FakeSchedule(Scheduled("4", minutes: 12), Scheduled("9", minutes: 20));
        var aggregator = Create([down], schedule);

        var result = await aggregator.GetArrivalsAsync("3490");

        Assert.Equal(2, result.Arrivals.Count);
        Assert.All(result.Arrivals, arrival => Assert.False(arrival.IsLive));
        Assert.Equal(ProviderStatus.Unavailable, result.ProviderStatuses[ArrivalSource.Curlbus]);
        Assert.Equal(ProviderStatus.Ok, result.ProviderStatuses[ArrivalSource.Schedule]);
    }

    [Fact]
    public async Task ThrowingProvider_DoesNotBreakAggregation()
    {
        var throwing = new FakeProvider(ArrivalSource.Curlbus) { Throw = true };
        var schedule = new FakeSchedule(Scheduled("4", minutes: 12));
        var aggregator = Create([throwing], schedule);

        var result = await aggregator.GetArrivalsAsync("3490");

        Assert.Single(result.Arrivals);
        Assert.Equal(ProviderStatus.Unavailable, result.ProviderStatuses[ArrivalSource.Curlbus]);
    }

    [Fact]
    public async Task LineWithLiveData_DoesNotAlsoShowItsScheduledTime()
    {
        var curlbus = new FakeProvider(ArrivalSource.Curlbus, Live(ArrivalSource.Curlbus, "4", minutes: 10, observedAgo: 20));
        var schedule = new FakeSchedule(Scheduled("4", minutes: 11), Scheduled("9", minutes: 15));
        var aggregator = Create([curlbus], schedule);

        var result = await aggregator.GetArrivalsAsync("3490");

        Assert.Equal(2, result.Arrivals.Count);
        Assert.Single(result.Arrivals, arrival => arrival.LineName == "4" && arrival.IsLive);
        Assert.Single(result.Arrivals, arrival => arrival.LineName == "9" && !arrival.IsLive);
        Assert.Equal(result.Arrivals.OrderBy(a => a.Eta).ToArray(), result.Arrivals);
    }

    [Fact]
    public async Task SameLineFromTwoProviders_PicksTheFreshest()
    {
        var curlbus = new FakeProvider(ArrivalSource.Curlbus, Live(ArrivalSource.Curlbus, "4", minutes: 10, observedAgo: 120));
        var stride = new FakeProvider(ArrivalSource.Stride, Live(ArrivalSource.Stride, "4", minutes: 9, observedAgo: 20));
        var aggregator = Create([curlbus, stride]);

        var arrival = Assert.Single((await aggregator.GetArrivalsAsync("3490")).Arrivals);

        Assert.Equal(ArrivalSource.Stride, arrival.Source);
    }

    [Fact]
    public async Task SameLineWithEqualFreshness_PrefersHigherPriorityProvider()
    {
        var curlbus = new FakeProvider(ArrivalSource.Curlbus, Live(ArrivalSource.Curlbus, "4", minutes: 10, observedAgo: 60));
        var stride = new FakeProvider(ArrivalSource.Stride, Live(ArrivalSource.Stride, "4", minutes: 9, observedAgo: 60));
        var aggregator = Create([stride, curlbus]);

        var arrival = Assert.Single((await aggregator.GetArrivalsAsync("3490")).Arrivals);

        Assert.Equal(ArrivalSource.Curlbus, arrival.Source);
    }

    [Fact]
    public async Task ScheduledArrivalInThePast_IsNotShown()
    {
        var schedule = new FakeSchedule(Scheduled("4", minutes: -30), Scheduled("9", minutes: 5));
        var aggregator = Create([], schedule);

        var arrival = Assert.Single((await aggregator.GetArrivalsAsync("3490")).Arrivals);

        Assert.Equal("9", arrival.LineName);
    }

    private static LiveDataAggregator Create(IEnumerable<ILiveArrivalsProvider> providers, IScheduleProvider? schedule = null) =>
        new(providers, schedule, new FakeClock(Now), NullLogger<LiveDataAggregator>.Instance);

    private static Arrival Live(ArrivalSource source, string line, int minutes, int observedAgo) =>
        new("3490", line, "Dan", "Somewhere", Now.AddMinutes(minutes), IsLive: true, source, Now.AddSeconds(-observedAgo));

    private static Arrival Scheduled(string line, int minutes) =>
        new("3490", line, "Dan", "Somewhere", Now.AddMinutes(minutes), IsLive: false, ArrivalSource.Schedule, Now);

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class FakeProvider(ArrivalSource source, params Arrival[] arrivals) : ILiveArrivalsProvider
    {
        public ArrivalSource Source { get; } = source;
        public ProviderStatus Status { get; init; } = ProviderStatus.Ok;
        public bool Throw { get; init; }

        public Task<ProviderResult> GetArrivalsAsync(string stopCode, CancellationToken cancellationToken = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(Status == ProviderStatus.Ok
                ? new ProviderResult(ProviderStatus.Ok, arrivals)
                : ProviderResult.Failed(Status, "down"));
        }
    }

    private sealed class FakeSchedule(params Arrival[] arrivals) : IScheduleProvider
    {
        public Task<ProviderResult> GetScheduleAsync(string stopCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderResult(ProviderStatus.Ok, arrivals));
    }
}
