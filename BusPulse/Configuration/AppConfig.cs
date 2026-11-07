using System.IO;
using System.Text.Json.Serialization;

namespace BusPulse.Configuration;

public sealed class AppConfig
{
    public string Language { get; set; } = "en";
    public string ActiveProfile { get; set; } = "Default";
    public bool NotificationsMuted { get; set; }
    public List<SavedPlace> SavedPlaces { get; set; } = [];
    public string? StartPlaceId { get; set; }
    public string? DestinationPlaceId { get; set; }
    public string? LineName { get; set; }
    public string? HomePlaceId { get; set; }
    public string? WorkPlaceId { get; set; }

    /// <summary>Seconds between live refreshes while a view is visible (15-60).</summary>
    public int PollingSeconds { get; set; } = 20;

    /// <summary>Minutes before the leave-by time at which a departure reminder fires (0-15).</summary>
    public int LeadMinutes { get; set; } = 10;

    /// <summary>Minutes to be at the stop before the bus is due (0-15).</summary>
    public int BufferMinutes { get; set; } = 1;

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";
    public List<string> FavoriteStopCodes { get; set; } = [];
    public List<string> FavoriteLines { get; set; } = [];

    [JsonPropertyName("StopCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyStopCode { get; set; }

    public string? ApiEndpoint { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>Google OAuth client for the read-only calendar link. Environment variables BUSPULSE_GOOGLE_CLIENT_ID / _SECRET take precedence.</summary>
    public string? GoogleClientId { get; set; }

    public string? GoogleClientSecret { get; set; }

    /// <summary>Whether typed addresses may be sent to the geocoding service (Settings switch).</summary>
    public bool AddressSearchEnabled { get; set; } = true;

    /// <summary>Address search server (Nominatim API). Must be an absolute https URL so it can be redirected without an update.</summary>
    public string GeocoderBaseUrl { get; set; } = "https://nominatim.openstreetmap.org/";

    public void Validate()
    {
        if (Language is not ("en" or "he"))
        {
            throw new InvalidDataException("Language must be 'en' or 'he'.");
        }

        if (string.IsNullOrWhiteSpace(ActiveProfile) || ActiveProfile.Length > 64)
        {
            throw new InvalidDataException("The active profile is invalid.");
        }

        if (SavedPlaces is null || SavedPlaces.Count > 30)
        {
            throw new InvalidDataException("The saved places list is invalid.");
        }

        foreach (var place in SavedPlaces)
        {
            place.Validate();
        }

        if (SavedPlaces.Select(place => place.Id).Distinct(StringComparer.Ordinal).Count() != SavedPlaces.Count)
        {
            throw new InvalidDataException("Saved place IDs must be unique.");
        }

        if (StartPlaceId is not null && !SavedPlaces.Any(place => place.Id == StartPlaceId))
        {
            throw new InvalidDataException("The selected start place does not exist.");
        }

        if (DestinationPlaceId is not null && !SavedPlaces.Any(place => place.Id == DestinationPlaceId))
        {
            throw new InvalidDataException("The selected destination does not exist.");
        }

        if (LineName?.Length > 64)
        {
            throw new InvalidDataException("The line name is too long.");
        }

        if (HomePlaceId is not null && !SavedPlaces.Any(place => place.Id == HomePlaceId))
        {
            throw new InvalidDataException("The home place does not exist.");
        }

        if (WorkPlaceId is not null && !SavedPlaces.Any(place => place.Id == WorkPlaceId))
        {
            throw new InvalidDataException("The work place does not exist.");
        }

        if (PollingSeconds is < 15 or > 60 || LeadMinutes is < 0 or > 15 || BufferMinutes is < 0 or > 15)
        {
            throw new InvalidDataException("A timing setting is out of range.");
        }

        if (GoogleClientId?.Length > 200 || GoogleClientSecret?.Length > 200)
        {
            throw new InvalidDataException("The Google client settings are too long.");
        }

        if (GeocoderBaseUrl is null || GeocoderBaseUrl.Length > 200 ||
            !Uri.TryCreate(GeocoderBaseUrl, UriKind.Absolute, out var geocoderUri) || geocoderUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The geocoder address must be an absolute https URL of at most 200 characters.");
        }

        if (Theme is not ("system" or "light" or "dark"))
        {
            throw new InvalidDataException("Theme must be 'system', 'light' or 'dark'.");
        }

        if (FavoriteStopCodes is null || FavoriteStopCodes.Count > 50 ||
            FavoriteLines is null || FavoriteLines.Count > 50 ||
            FavoriteStopCodes.Concat(FavoriteLines).Any(item => string.IsNullOrWhiteSpace(item) || item.Length > 32))
        {
            throw new InvalidDataException("The favorites list is invalid.");
        }
    }
}