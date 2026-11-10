using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Trips.Gtfs;

/// <summary>Line colours from the Ministry of Transport feed (<c>route_color</c>), looked up by line number and operator.</summary>
public interface IRouteColors
{
    /// <summary>"RRGGBB" or null. Null when the feed has no colour for the line, or its routes disagree: nothing is invented.</summary>
    string? Find(string? line, string? operatorName);
}

/// <summary>
/// Keeps a small map (operator + line number to route_color) next to the cached feed. It is read from a feed zip that is
/// already on disk and never starts a download. A key whose routes carry different colours (or a colour on only some of
/// them) is dropped, because the same number is reused by different routes of one operator.
/// </summary>
public sealed class RouteColorCatalog : IRouteColors
{
    private const int MaxEntries = 50_000;
    private const string FileName = "route-colors.json";
    private const long MaxSmallBytes = 100_000_000;
    private readonly string _cacheDirectory;
    private readonly ILogger<RouteColorCatalog>? _logger;
    private readonly object _gate = new();
    private Dictionary<string, string> _colors = new(StringComparer.Ordinal);
    private bool _loaded;

    public RouteColorCatalog(ILogger<RouteColorCatalog>? logger = null, string? cacheDirectory = null)
    {
        _logger = logger;
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusPulse", "cache");
    }

    public string? Find(string? line, string? operatorName)
    {
        if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(operatorName))
        {
            return null;
        }

        EnsureLoaded();
        lock (_gate)
        {
            return _colors.GetValueOrDefault(Key(operatorName, line));
        }
    }

    /// <summary>Replaces the map from a feed zip (routes.txt and agency.txt only) and saves it. False when the zip is unreadable.</summary>
    public bool RefreshFromZip(string zipPath)
    {
        try
        {
            var colors = ReadColors(zipPath);
            lock (_gate)
            {
                _colors = colors;
                _loaded = true;
            }

            Directory.CreateDirectory(_cacheDirectory);
            var temp = Path.Combine(_cacheDirectory, $"route-colors-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temp, JsonSerializer.Serialize(colors));
            File.Move(temp, Path.Combine(_cacheDirectory, FileName), overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogDebug(exception, "Route colours could not be read from the feed.");
            return false;
        }
    }

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
        }

        try
        {
            var path = Path.Combine(_cacheDirectory, FileName);
            if (File.Exists(path) && new FileInfo(path).Length < 10_000_000)
            {
                var read = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                if (read is not null && read.Count <= MaxEntries)
                {
                    lock (_gate)
                    {
                        _colors = new Dictionary<string, string>(read, StringComparer.Ordinal);
                    }

                    return;
                }
            }

            // First run after an update: a feed zip downloaded earlier may already be on disk.
            if (Directory.Exists(_cacheDirectory) &&
                Directory.EnumerateFiles(_cacheDirectory, "gtfs-????-??-??.zip").OrderByDescending(file => file).FirstOrDefault() is { } zip)
            {
                RefreshFromZip(zip);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.LogDebug(exception, "Route colours could not be loaded.");
        }
    }

    private static string Key(string operatorName, string line) =>
        $"{operatorName.Trim().ToUpperInvariant()}|{line.Trim().ToUpperInvariant()}";

    internal static Dictionary<string, string> ReadColors(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var agencies = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = Open(zip, "agency.txt", out var splitter, out var header))
        {
            var id = header.Find("agency_id");
            var name = header.Require("agency_name", "agency.txt");
            while (reader.TryRead(out var line))
            {
                splitter.Split(line);
                agencies[id >= 0 ? splitter.Text(line, id, 64) : string.Empty] = splitter.Text(line, name);
            }
        }

        // Per key: the single colour all its routes share, or null once they disagree or one has none.
        var seen = new Dictionary<string, string?>(StringComparer.Ordinal);
        using (var reader = Open(zip, "routes.txt", out var splitter, out var header))
        {
            var agency = header.Find("agency_id");
            var shortName = header.Require("route_short_name", "routes.txt");
            var color = header.Find("route_color");
            while (reader.TryRead(out var line))
            {
                splitter.Split(line);
                var agencyName = agency >= 0 && agencies.TryGetValue(splitter.Text(line, agency, 64), out var a)
                    ? a
                    : agencies.Count == 1 ? agencies.Values.First() : null;
                var lineName = splitter.Text(line, shortName);
                if (agencyName is null || lineName.Length == 0)
                {
                    continue;
                }

                var hex = color >= 0 ? splitter.Text(line, color, 16).TrimStart('#').ToUpperInvariant() : string.Empty;
                var value = hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex : null;
                var key = Key(agencyName, lineName);
                seen[key] = seen.TryGetValue(key, out var previous) ? (previous == value ? value : null) : value;
                if (seen.Count > MaxEntries * 2)
                {
                    throw new InvalidDataException("The feed has too many routes.");
                }
            }
        }

        return seen.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal);
    }

    private static GtfsLineReader Open(ZipArchive zip, string name, out GtfsCsvSplitter splitter, out GtfsHeader header)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"The feed has no {name}.");
        var reader = new GtfsLineReader(new GtfsLimitedStream(entry.Open(), MaxSmallBytes, name));
        splitter = new GtfsCsvSplitter();
        header = GtfsHeader.Read(reader, splitter, name);
        return reader;
    }
}
