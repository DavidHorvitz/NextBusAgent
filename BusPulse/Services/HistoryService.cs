using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace BusPulse.Services;

public sealed record HistoryEntry(string PlaceId, string Name, DateTimeOffset UsedAt);

public interface IHistoryService
{
    /// <summary>Most recently used destinations first.</summary>
    IReadOnlyList<HistoryEntry> Recent { get; }

    void Record(string placeId, string name);

    void Clear();
}

/// <summary>Recent destinations, stored only on this machine in user_history.json with a size cap.</summary>
public sealed class HistoryService : IHistoryService
{
    public const int MaxEntries = 30;
    private const int MaxFileBytes = 64 * 1024;

    private readonly string _path;
    private readonly IClock _clock;
    private readonly ILogger<HistoryService> _logger;
    private readonly object _gate = new();
    private List<HistoryEntry> _entries;

    public HistoryService(IClock clock, ILogger<HistoryService> logger, string? path = null)
    {
        _clock = clock;
        _logger = logger;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusPulse", "user_history.json");
        _entries = Load();
    }

    public IReadOnlyList<HistoryEntry> Recent
    {
        get
        {
            lock (_gate)
            {
                return _entries.OrderByDescending(entry => entry.UsedAt).ToArray();
            }
        }
    }

    public void Record(string placeId, string name)
    {
        if (string.IsNullOrWhiteSpace(placeId) || placeId.Length > 64 || string.IsNullOrWhiteSpace(name) || name.Length > 64)
        {
            return;
        }

        lock (_gate)
        {
            _entries.RemoveAll(entry => entry.PlaceId == placeId);
            _entries.Add(new HistoryEntry(placeId, name, _clock.UtcNow));
            _entries = _entries.OrderByDescending(entry => entry.UsedAt).Take(MaxEntries).ToList();
            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = [];
            try
            {
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Could not delete the history file.");
            }
        }
    }

    private List<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > MaxFileBytes)
            {
                return [];
            }

            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllBytes(_path), new JsonSerializerOptions { MaxDepth = 8 });
            return (entries ?? [])
                .Where(entry => !string.IsNullOrWhiteSpace(entry.PlaceId) && entry.PlaceId.Length <= 64 &&
                    !string.IsNullOrWhiteSpace(entry.Name) && entry.Name.Length <= 64)
                .OrderByDescending(entry => entry.UsedAt)
                .Take(MaxEntries)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(exception, "Ignoring an unreadable history file.");
            return [];
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_entries));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not save the history file.");
        }
    }
}
