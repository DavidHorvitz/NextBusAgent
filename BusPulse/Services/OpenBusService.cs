using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace BusPulse.Services;

public sealed class OpenBusService(IHttpClientFactory httpClientFactory)
{
    private const string ApiBaseAddress = "https://openbus-stride-api.hasadna.org.il/";
    private const int MaxResponseBytes = 1_048_576;
    private static readonly string[] LinePropertyNames =
    [
        "line_name",
        "published_line_name",
        "line_ref",
        "line"
    ];

    public async Task<IReadOnlyList<JsonElement>> GetBusArrivalsAsync(
        string stopId,
        string lineName,
        CancellationToken cancellationToken = default)
    {
        ValidateInput(stopId, lineName);
        var requestUri = new Uri(
            ApiBaseAddress + "siri_rides/list?stop_code=" + Uri.EscapeDataString(stopId) + "&limit=5",
            UriKind.Absolute);
        var client = httpClientFactory.CreateClient("TransitApi");
        using var response = await client.GetAsync(
            requestUri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
        {
            throw new JsonException("OpenBus response exceeds the configured size limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + bytesRead > MaxResponseBytes)
            {
                throw new JsonException("OpenBus response exceeds the configured size limit.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken);
        }

        using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions
        {
            MaxDepth = 32,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        });

        return FindRideRows(document.RootElement)
            .Where(row => MatchesLine(row, lineName))
            .Select(row => row.Clone())
            .ToArray();
    }

    private static void ValidateInput(string stopId, string lineName)
    {
        if (string.IsNullOrWhiteSpace(stopId) || stopId.Length > 32 ||
            stopId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
        {
            throw new ArgumentException("The stop code is invalid.", nameof(stopId));
        }

        if (string.IsNullOrWhiteSpace(lineName) || lineName.Length > 64)
        {
            throw new ArgumentException("The line name is invalid.", nameof(lineName));
        }
    }

    private static IEnumerable<JsonElement> FindRideRows(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().ToArray();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("OpenBus response must contain an object or array.");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array &&
                property.Name is "results" or "data" or "rides" or "siri_rides")
            {
                return property.Value.EnumerateArray().ToArray();
            }
        }

        return [root];
    }

    private static bool MatchesLine(JsonElement row, string lineName)
    {
        if (row.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in row.EnumerateObject())
        {
            if (LinePropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                string.Equals(property.Value.GetString()?.Trim(), lineName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}