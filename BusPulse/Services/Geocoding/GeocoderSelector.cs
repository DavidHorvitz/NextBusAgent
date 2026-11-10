namespace BusPulse.Services.Geocoding;

/// <summary>
/// The geocoder the app uses. With a Google Places key it gives as-you-type suggestions; without one it offers only
/// the explicit Nominatim lookup. An explicit search (Enter, leaving the field) falls back to Nominatim when Google
/// is paused, capped or failing; as-you-type suggestions never do (the public Nominatim server forbids them).
/// </summary>
public sealed class GeocoderSelector : IGeocoder
{
    private readonly NominatimGeocoder _nominatim;
    private readonly GooglePlacesGeocoder _google;

    public GeocoderSelector(NominatimGeocoder nominatim, GooglePlacesGeocoder google)
    {
        _nominatim = nominatim;
        _google = google;
        // Read the key in the background so the first keystrokes already know whether suggestions are available.
        _ = Task.Run(async () =>
        {
            try
            {
                await _google.EnsureKeyAsync();
            }
            catch (Exception)
            {
                // A missing or unreachable secret only leaves the Google provider off.
            }
        });
    }

    public bool SupportsAutocomplete => _google.SupportsAutocomplete;

    public Task<GeocodeResult> SuggestAsync(string query, CancellationToken cancellationToken = default) =>
        _google.SupportsAutocomplete ? _google.SuggestAsync(query, cancellationToken) : Task.FromResult(GeocodeResult.Of(GeocodeStatus.Disabled));

    public async Task<GeocodeResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (_google.SupportsAutocomplete)
        {
            var result = await _google.SearchAsync(query, cancellationToken);
            if (result.Status is not (GeocodeStatus.Unavailable or GeocodeStatus.RateLimited or GeocodeStatus.DailyCapReached or GeocodeStatus.Disabled))
            {
                return result;
            }
        }

        return await _nominatim.SearchAsync(query, cancellationToken);
    }

    public Task<GeocodedPlace?> ResolveAsync(GeocodedPlace place, CancellationToken cancellationToken = default) =>
        place.IsUnresolved ? _google.ResolveAsync(place, cancellationToken) : Task.FromResult<GeocodedPlace?>(place);
}
