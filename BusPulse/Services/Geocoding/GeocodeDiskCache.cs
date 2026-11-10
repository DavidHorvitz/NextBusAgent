using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services.Geocoding;

/// <summary>
/// 24-hour on-disk cache of address search results, so an identical query is never sent twice, even after a restart.
/// Queries are stored only as SHA-256 hashes and the whole file is DPAPI-encrypted for the current Windows user
/// (%LOCALAPPDATA%\BusPulse\cache\geocode.dpapi): the places a person looked up are not readable from the file.
/// </summary>
public sealed class GeocodeDiskCache
{
    public const int MaxEntries = 100;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private const int MaxFileBytes = 256 * 1024;
    private static readonly byte[] Entropy = "BusPulse.Geocode.v1"u8.ToArray();

    public sealed record Entry(DateTimeOffset StoredAt, bool NoResults, List<GeocodedPlace> Places);

    private readonly string _path;
    private readonly IClock _clock;
    private readonly ILogger<GeocodeDiskCache>? _logger;
    private readonly object _gate = new();
    private Dictionary<string, Entry>? _entries;

    public GeocodeDiskCache(IClock clock, ILogger<GeocodeDiskCache>? logger = null, string? path = null)
    {
        _clock = clock;
        _logger = logger;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusPulse", "cache", "geocode.dpapi");
    }

    public static string HashKey(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    public bool TryGet(string key, out GeocodeResult result)
    {
        lock (_gate)
        {
            if (Load().TryGetValue(HashKey(key), out var entry) && _clock.UtcNow - entry.StoredAt < Lifetime)
            {
                result = entry.NoResults ? GeocodeResult.Of(GeocodeStatus.NoResults) : new GeocodeResult(GeocodeStatus.Ok, entry.Places);
                return true;
            }
        }

        result = null!;
        return false;
    }

    public void Store(string key, GeocodeResult result)
    {
        if (result.Status is not (GeocodeStatus.Ok or GeocodeStatus.NoResults))
        {
            return;
        }

        lock (_gate)
        {
            var entries = Load();
            var now = _clock.UtcNow;
            foreach (var expired in entries.Where(pair => now - pair.Value.StoredAt >= Lifetime).Select(pair => pair.Key).ToList())
            {
                entries.Remove(expired);
            }

            var hash = HashKey(key);
            while (entries.Count >= MaxEntries && !entries.ContainsKey(hash))
            {
                entries.Remove(entries.MinBy(pair => pair.Value.StoredAt)!.Key);
            }

            entries[hash] = new Entry(now, result.Status == GeocodeStatus.NoResults, result.Places.ToList());
            Save(entries);
        }
    }

    private Dictionary<string, Entry> Load()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        _entries = [];
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length is > 0 and <= MaxFileBytes)
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(json) ?? [];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            // A damaged or foreign cache is ignored; it is rebuilt from the next searches.
            _logger?.LogWarning("The address cache could not be read ({Kind}); starting empty.", exception.GetType().Name);
            _entries = [];
        }

        return _entries;
    }

    private void Save(Dictionary<string, Entry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(entries), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path, bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            _logger?.LogWarning("The address cache could not be written ({Kind}).", exception.GetType().Name);
        }
    }
}
