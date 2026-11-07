using System.IO;
using BusPulse.Services.Stops;

namespace BusPulse.Configuration;

public sealed class SavedPlace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;

    /// <summary>Empty means the place has no stop code and is defined by its coordinates.</summary>
    public string StopCode { get; set; } = string.Empty;

    /// <summary>When both coordinates are present they win over the stop code as the place's point.</summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? Address { get; set; }

    /// <summary>What to show next to the name: the address when there is one, else the stop code, else nothing.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayDetail => !string.IsNullOrWhiteSpace(Address) ? Address : StopCode;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64 ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 64)
        {
            throw new InvalidDataException("A saved place name or ID is invalid.");
        }

        if (StopCode is null || StopCode.Length > 32 ||
            StopCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new InvalidDataException("The OpenBus stop code contains invalid characters.");
        }

        if (Address is { Length: > 200 })
        {
            throw new InvalidDataException("The saved place address is too long.");
        }

        if (Latitude.HasValue != Longitude.HasValue)
        {
            throw new InvalidDataException("A saved place needs both a latitude and a longitude.");
        }

        if (Latitude is { } latitude && Longitude is { } longitude)
        {
            if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || !Geo.IsInIsrael(latitude, longitude))
            {
                throw new InvalidDataException("The saved place coordinates are outside Israel.");
            }
        }
        else if (StopCode.Length == 0)
        {
            throw new InvalidDataException("A saved place needs a stop code or coordinates.");
        }
    }
}