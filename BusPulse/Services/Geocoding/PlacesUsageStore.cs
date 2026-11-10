using System.IO;
using System.Text.Json;
using BusPulse.Services.Trips;

namespace BusPulse.Services.Geocoding;

/// <summary>
/// Counts Google Places autocomplete sessions per Israel calendar day in %LOCALAPPDATA%\BusPulse\places_usage.json
/// (a date and a number, nothing else) so the local cap survives a restart. At the cap no further session starts.
/// </summary>
public sealed class PlacesUsageStore(IClock clock, string? path = null)
{
    public const int DefaultDailyCap = 200;

    private sealed record Usage(string Date, int Sessions);

    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusPulse", "places_usage.json");
    private readonly object _gate = new();
    private Usage? _usage;

    private string Today => TripTime.TodayIn(clock.UtcNow, TripTime.IsraelZone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public int SessionsToday
    {
        get
        {
            lock (_gate)
            {
                return Current().Sessions;
            }
        }
    }

    /// <summary>Counts a new session and returns true, or returns false (counting nothing) when the day's cap is reached.</summary>
    public bool TryStartSession(int cap)
    {
        lock (_gate)
        {
            var current = Current();
            if (current.Sessions >= cap)
            {
                return false;
            }

            _usage = current with { Sessions = current.Sessions + 1 };
            Save();
            return true;
        }
    }

    private Usage Current()
    {
        if (_usage is null)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length is > 0 and < 1024)
                {
                    _usage = JsonSerializer.Deserialize<Usage>(File.ReadAllText(_path));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                _usage = null;
            }
        }

        if (_usage is null || _usage.Date != Today || _usage.Sessions < 0)
        {
            _usage = new Usage(Today, 0);
        }

        return _usage;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_usage));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The in-memory count still protects this run.
        }
    }
}
