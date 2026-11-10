using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;

namespace BusPulse.Tests;

internal sealed class FixedClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

internal sealed class ImmediateDispatcher : IDispatcher
{
    public void Post(Action action) => action();
}

internal sealed class FakeLocation : ILocationService
{
    public int Calls { get; private set; }
    public LocationResult Result { get; set; } = LocationResult.Failed(LocationStatus.Unavailable);

    public Task<LocationResult> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

internal sealed class FakeCatalog(params Stop[] stops) : IStopCatalog
{
    public Exception? Throw { get; set; }

    public IReadOnlyList<Stop>? Loaded => Throw is null ? stops : null;

    public Task<IReadOnlyList<Stop>> GetStopsAsync(CancellationToken cancellationToken = default) =>
        Throw is null ? Task.FromResult<IReadOnlyList<Stop>>(stops) : Task.FromException<IReadOnlyList<Stop>>(Throw);
}

internal sealed class MemorySettings : ISettingsService
{
    public AppConfig Config { get; } = new();
    public int SaveCount { get; private set; }
    public bool SaveSucceeds { get; set; } = true;

    public Task<bool> SaveAsync()
    {
        SaveCount++;
        return Task.FromResult(SaveSucceeds);
    }
}

/// <summary>Returns the key for most lookups and a real template for keys whose format arguments tests assert on.</summary>
internal sealed class TemplateLocalizer : ILocalizer
{
    private static readonly Dictionary<string, string> Templates = new()
    {
        ["DistanceMeters"] = "{0} m",
        ["EtaMinutes"] = "{0}m",
        ["EtaNow"] = "now",
        ["UpdatedAgo"] = "{0}s ago",
        ["LocationAccuracy"] = "accuracy {0}",
        ["LocationFromPlace"] = "near {0}",
        ["LeaveInMinutes"] = "leave in {0}m",
        ["LeaveAtClock"] = "leave at {0}",
        ["LeaveNow"] = "leave now",
        ["BusAt"] = "bus {0}",
        ["ArriveAt"] = "arrive {0}",
        ["WalkTo"] = "{0} min walk to {1}",
        ["ToastLineIn"] = "Line {0} in {1} min",
        ["ToastLeaveBy"] = "Leave by {0} · {1}",
        ["ToastDelayed"] = "Line {0} delayed by {1} min · now at {2}",
        ["ToastAlertAdded"] = "Alert added: Line {0} to {1}",
        ["AlertUpcoming"] = "Line {0} departs in {1} min",
        ["AlertSoon"] = "Line {0} departs in {1} min — you should leave soon",
        ["AlertLeaveNow"] = "Line {0} departs in {1} min — leave NOW",
        ["AlertPopupBody"] = "LEAVE NOW — Line {0} in {1} min!",
        ["AlertPopupUnderMinute"] = "LEAVE NOW — Line {0} in under a minute!",
        ["AlertLate"] = "You are LATE — Line {0} departs in {1} seconds. HURRY!",
        ["AlertMissed"] = "You missed Line {0}.",
        ["AlertMissedNext"] = "You missed Line {0}. Next option: Line {1} in {2} min",
        ["AlertWalkDetail"] = "{0} min walk + {1} min wait = {2} min before departure · leave in {3} min",
        ["AlertAtStop"] = "Stop: {0}",
        ["WalkUnderMinute"] = "Less than 1 min walk to {0}"
    };

    public string Language { get; private set; } = "en";

    public event EventHandler? LanguageChanged;

    public string Get(string key) => Templates.GetValueOrDefault(key, key);

    public void SetLanguage(string language)
    {
        Language = language;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeHistory : IHistoryService
{
    public List<HistoryEntry> Entries { get; } = [];
    public int ClearCalls { get; private set; }

    public IReadOnlyList<HistoryEntry> Recent => Entries;

    public void Record(string placeId, string name)
    {
        Entries.RemoveAll(entry => entry.PlaceId == placeId);
        Entries.Insert(0, new HistoryEntry(placeId, name, DateTimeOffset.UtcNow));
    }

    public void Clear()
    {
        ClearCalls++;
        Entries.Clear();
    }
}

internal sealed class FakeReminder : IDepartureReminder
{
    public TripOption? Watched { get; private set; }
    public TripRequest? Request { get; private set; }

    public event EventHandler? Changed;

    public void Watch(TripRequest request, TripOption option)
    {
        Request = request;
        Watched = option;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        Watched = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task<BusPulse.Services.Notifications.AlertMessage?> TickAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<BusPulse.Services.Notifications.AlertMessage?>(null);
}

internal sealed class FakeTripPlanner : ITripPlannerService
{
    public TripPlan Plan { get; set; } = new(TripPlanStatus.NoOptions, []);
    public TripRequest? LastRequest { get; private set; }
    public int Calls { get; private set; }

    /// <summary>Thrown from PlanAsync when set (for example a timeout's TaskCanceledException).</summary>
    public Exception? Throw { get; set; }

    /// <summary>Runs inside PlanAsync, to simulate something happening while a plan is in flight.</summary>
    public Action? OnPlan { get; set; }

    public Task<TripPlan> PlanAsync(TripRequest request, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastRequest = request;
        OnPlan?.Invoke();
        return Throw is null ? Task.FromResult(Plan) : Task.FromException<TripPlan>(Throw);
    }
}

/// <summary>Tags every lookup with the active language so tests can see whether text was refreshed.</summary>
internal sealed class LanguageAwareLocalizer : ILocalizer
{
    public string Language { get; private set; } = "en";

    public event EventHandler? LanguageChanged;

    public string Get(string key) => $"{key}:{Language}";

    public void SetLanguage(string language)
    {
        Language = language;
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class FakeTheme : IThemeService
{
    public List<string> Applied { get; } = [];

    public void Apply(string theme) => Applied.Add(theme);

    public void Attach(System.Windows.Window window)
    {
    }
}

internal sealed class FakeCalendar : BusPulse.Services.Calendar.ICalendarService
{
    public bool IsConfigured { get; set; } = true;
    public bool IsConnected { get; set; }
    public BusPulse.Services.Calendar.CalendarConnectResult Result { get; set; } = new(BusPulse.Services.Calendar.CalendarConnectStatus.Connected);
    public int Disconnects { get; private set; }

    public Task<BusPulse.Services.Calendar.CalendarConnectResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (Result.Status == BusPulse.Services.Calendar.CalendarConnectStatus.Connected)
        {
            IsConnected = true;
        }

        return Task.FromResult(Result);
    }

    public List<BusPulse.Services.Calendar.CalendarEvent> Events { get; set; } = [];
    public Exception? Throw { get; set; }
    public int UpcomingCalls { get; private set; }

    public Task<IReadOnlyList<BusPulse.Services.Calendar.CalendarEvent>> GetUpcomingAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        UpcomingCalls++;
        return Throw is null
            ? Task.FromResult<IReadOnlyList<BusPulse.Services.Calendar.CalendarEvent>>(Events)
            : Task.FromException<IReadOnlyList<BusPulse.Services.Calendar.CalendarEvent>>(Throw);
    }

    public void Disconnect()
    {
        Disconnects++;
        IsConnected = false;
    }
}

/// <summary>Address search without the network: returns <see cref="Result"/> and records every query it was asked.</summary>
internal sealed class FakeGeocoder : BusPulse.Services.Geocoding.IGeocoder
{
    public BusPulse.Services.Geocoding.GeocodeResult Result { get; set; } = BusPulse.Services.Geocoding.GeocodeResult.Of(BusPulse.Services.Geocoding.GeocodeStatus.NoResults);

    public List<string> Queries { get; } = [];

    /// <summary>Behaves like Google Places: suggestions while typing.</summary>
    public bool Autocomplete { get; set; }

    public List<string> SuggestQueries { get; } = [];

    public List<BusPulse.Services.Geocoding.GeocodedPlace> Resolved { get; } = [];

    /// <summary>When set, a lookup waits for this task, so a test can observe the in-flight state.</summary>
    public Task? Gate { get; set; }

    public bool SupportsAutocomplete => Autocomplete;

    public async Task<BusPulse.Services.Geocoding.GeocodeResult> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        if (Gate is not null)
        {
            await Gate.WaitAsync(cancellationToken);
        }

        return Result;
    }

    public async Task<BusPulse.Services.Geocoding.GeocodeResult> SuggestAsync(string query, CancellationToken cancellationToken = default)
    {
        SuggestQueries.Add(query);
        if (Gate is not null)
        {
            await Gate.WaitAsync(cancellationToken);
        }

        return Result;
    }

    public Task<BusPulse.Services.Geocoding.GeocodedPlace?> ResolveAsync(BusPulse.Services.Geocoding.GeocodedPlace place, CancellationToken cancellationToken = default)
    {
        Resolved.Add(place);
        return Task.FromResult<BusPulse.Services.Geocoding.GeocodedPlace?>(place.IsUnresolved ? place with { Latitude = 32.07, Longitude = 34.78 } : place);
    }
}