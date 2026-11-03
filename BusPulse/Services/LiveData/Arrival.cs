namespace BusPulse.Services.LiveData;

public enum ArrivalSource
{
    Curlbus,
    Rail,
    Stride,
    Mot,
    Schedule
}

public sealed record Arrival(
    string StopCode,
    string LineName,
    string? OperatorName,
    string? Destination,
    DateTimeOffset Eta,
    bool IsLive,
    ArrivalSource Source,
    DateTimeOffset ObservedAt,
    string? TripRef = null);

public enum ProviderStatus
{
    Ok,
    NoData,
    Unavailable,
    CircuitOpen,
    Disabled
}

public sealed record ProviderResult(ProviderStatus Status, IReadOnlyList<Arrival> Arrivals, string? Detail = null)
{
    public static ProviderResult Failed(ProviderStatus status, string detail) => new(status, [], detail);
}
