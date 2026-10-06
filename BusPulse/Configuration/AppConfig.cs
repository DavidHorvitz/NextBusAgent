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

    [JsonPropertyName("StopCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyStopCode { get; set; }

    public string? ApiEndpoint { get; set; }
    public string? ApiKey { get; set; }

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
    }
}