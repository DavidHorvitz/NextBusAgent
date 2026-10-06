using System.IO;

namespace BusPulse.Configuration;

public sealed class SavedPlace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string StopCode { get; set; } = string.Empty;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 64 ||
            string.IsNullOrWhiteSpace(Name) || Name.Length > 64)
        {
            throw new InvalidDataException("A saved place name or ID is invalid.");
        }

        if (StopCode.Length is 0 or > 32 ||
            StopCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new InvalidDataException("The OpenBus stop code contains invalid characters.");
        }
    }
}