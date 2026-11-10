namespace BusPulse.Services.Geocoding;

public enum GeocodeStatus
{
    Ok,
    NoResults,

    /// <summary>The user switched address search off in Settings; nothing was sent.</summary>
    Disabled,

    /// <summary>The service could not be reached or answered with something unusable.</summary>
    Unavailable,

    /// <summary>The query is shorter than the minimum, so nothing was sent.</summary>
    TooShort,

    /// <summary>The service answered 429 or 403: calls to it are paused for ten minutes.</summary>
    RateLimited,

    /// <summary>The local daily cap on billable sessions is used up; nothing was sent.</summary>
    DailyCapReached
}

/// <summary>
/// A short, human-readable <paramref name="Label"/> and the coordinates of one address search hit.
/// A Google suggestion carries a <paramref name="PlaceId"/> and no coordinates until it is resolved.
/// </summary>
public sealed record GeocodedPlace(string Label, double Latitude, double Longitude, string? PlaceId = null)
{
    /// <summary>A suggestion that still needs its coordinates (Place Details) before it can be used.</summary>
    public bool IsUnresolved => PlaceId is not null && Latitude == 0 && Longitude == 0;

    /// <summary>The label only: a list item is named by this text for a screen reader, and a logged value must not carry coordinates.</summary>
    public override string ToString() => Label;
}

public sealed record GeocodeResult(GeocodeStatus Status, IReadOnlyList<GeocodedPlace> Places)
{
    public static GeocodeResult Of(GeocodeStatus status) => new(status, []);
}

public interface IGeocoder
{
    /// <summary>
    /// Looks up an address typed by the user. Must be triggered explicitly (Enter or leaving the field), never while typing.
    /// Returns a status instead of throwing for service problems; only a cancelled token throws.
    /// </summary>
    Task<GeocodeResult> SearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>True only for a provider that allows suggestions while the user types (never the public Nominatim server).</summary>
    bool SupportsAutocomplete => false;

    /// <summary>As-you-type suggestions. Only called when <see cref="SupportsAutocomplete"/> is true; otherwise nothing is sent.</summary>
    Task<GeocodeResult> SuggestAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult(GeocodeResult.Of(GeocodeStatus.Disabled));

    /// <summary>Fetches the coordinates of a suggestion that has none yet; a place that already has them is returned as is.</summary>
    Task<GeocodedPlace?> ResolveAsync(GeocodedPlace place, CancellationToken cancellationToken = default) =>
        Task.FromResult<GeocodedPlace?>(place);
}
