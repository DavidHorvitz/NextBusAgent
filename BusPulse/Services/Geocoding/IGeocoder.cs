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
    TooShort
}

/// <summary>A short, human-readable <paramref name="Label"/> and the coordinates of one address search hit.</summary>
public sealed record GeocodedPlace(string Label, double Latitude, double Longitude);

public sealed record GeocodeResult(GeocodeStatus Status, IReadOnlyList<GeocodedPlace> Places)
{
    public static GeocodeResult Of(GeocodeStatus status) => new(status, []);
}

public interface IGeocoder
{
    /// <summary>
    /// Looks up an address typed by the user. Must be triggered explicitly (button or Enter), never while typing.
    /// Returns a status instead of throwing for service problems; only a cancelled token throws.
    /// </summary>
    Task<GeocodeResult> SearchAsync(string query, CancellationToken cancellationToken = default);
}
