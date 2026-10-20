using System.Globalization;
using BusPulse.Infrastructure;
using BusPulse.Services.Trips;

namespace BusPulse.Services.Notifications;

public enum AlertKind
{
    Upcoming,
    Soon,
    LeaveNow,
    Popup,
    Late,
    Missed
}

public enum AlertUrgency
{
    Normal,
    Amber,
    Red
}

/// <summary>What to show right now; <see cref="FullScreen"/> alerts stay on screen until dismissed.</summary>
public sealed record AlertMessage(AlertKind Kind, string Title, string Message, AlertUrgency Urgency, bool FullScreen = false);

/// <summary>
/// Chooses the message for a watched trip from the time left. Stages follow the time to leave (walking and margin included):
/// more than 10 minutes "departs in X" every 30 s; up to 10 minutes "leave soon" every 20 s; within the lead time "leave NOW"
/// (amber) every 10 s; within 2 minutes one full-screen alert; too late to reach the stop "LATE" (red) every 10 s; after the bus
/// has gone "missed" once, naming the next option. Text comes from the localizer, so it follows the UI language.
/// </summary>
public sealed class SmartAlertEngine(ILocalizer localizer)
{
    public static readonly TimeSpan UpcomingInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan SoonInterval = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan UrgentInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PopupThreshold = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SoonThreshold = TimeSpan.FromMinutes(10);

    private readonly Dictionary<AlertKind, DateTimeOffset> _lastShown = [];

    /// <summary>Forget what was already shown; call when a different route is selected.</summary>
    public void Reset() => _lastShown.Clear();

    public AlertMessage? Evaluate(TripOption current, TripOption? next, DateTimeOffset now, int leadMinutes)
    {
        var busEta = current.BusDeparture - now;
        if (busEta <= TimeSpan.Zero)
        {
            return Once(AlertKind.Missed, now) ? BuildMissed(current, next, now) : null;
        }

        var timeToLeave = current.LeaveBy - now;
        var latestStart = current.BusDeparture - current.WalkToStop;
        if (now > latestStart)
        {
            return Due(AlertKind.Late, now, UrgentInterval) ? BuildLate(current, busEta) : null;
        }

        if (timeToLeave <= PopupThreshold)
        {
            return Once(AlertKind.Popup, now) ? BuildPopup(current, busEta) : null;
        }

        var lead = TimeSpan.FromMinutes(Math.Clamp(leadMinutes, 0, 15));
        if (timeToLeave <= lead)
        {
            return Due(AlertKind.LeaveNow, now, UrgentInterval) ? BuildTiered(AlertKind.LeaveNow, "AlertLeaveNow", AlertUrgency.Amber, current, busEta, timeToLeave) : null;
        }

        if (timeToLeave <= SoonThreshold)
        {
            return Due(AlertKind.Soon, now, SoonInterval) ? BuildTiered(AlertKind.Soon, "AlertSoon", AlertUrgency.Normal, current, busEta, timeToLeave) : null;
        }

        return Due(AlertKind.Upcoming, now, UpcomingInterval) ? BuildTiered(AlertKind.Upcoming, "AlertUpcoming", AlertUrgency.Normal, current, busEta, timeToLeave) : null;
    }

    private bool Due(AlertKind kind, DateTimeOffset now, TimeSpan interval)
    {
        if (_lastShown.TryGetValue(kind, out var last) && now - last < interval)
        {
            return false;
        }

        _lastShown[kind] = now;
        return true;
    }

    private bool Once(AlertKind kind, DateTimeOffset now)
    {
        if (_lastShown.ContainsKey(kind))
        {
            return false;
        }

        _lastShown[kind] = now;
        return true;
    }

    private AlertMessage BuildTiered(AlertKind kind, string key, AlertUrgency urgency, TripOption option, TimeSpan busEta, TimeSpan timeToLeave)
    {
        var title = string.Format(CultureInfo.CurrentCulture, localizer.Get(key), option.Line, Minutes(busEta));
        return new AlertMessage(kind, title, WalkDetail(option, timeToLeave), urgency);
    }

    private AlertMessage BuildPopup(TripOption option, TimeSpan busEta)
    {
        var title = busEta < TimeSpan.FromMinutes(1)
            ? string.Format(CultureInfo.CurrentCulture, localizer.Get("AlertPopupUnderMinute"), option.Line)
            : string.Format(CultureInfo.CurrentCulture, localizer.Get("AlertPopupBody"), option.Line, Minutes(busEta));
        return new AlertMessage(AlertKind.Popup, title, StopLine(option), AlertUrgency.Red, FullScreen: true);
    }

    private AlertMessage BuildLate(TripOption option, TimeSpan busEta)
    {
        var seconds = Math.Max(1, (int)Math.Round(busEta.TotalSeconds));
        return new AlertMessage(
            AlertKind.Late,
            string.Format(CultureInfo.CurrentCulture, localizer.Get("AlertLate"), option.Line, seconds),
            StopLine(option),
            AlertUrgency.Red);
    }

    private AlertMessage BuildMissed(TripOption missed, TripOption? next, DateTimeOffset now)
    {
        var message = next is null
            ? string.Format(CultureInfo.CurrentCulture, localizer.Get("AlertMissed"), missed.Line)
            : string.Format(CultureInfo.CurrentCulture, localizer.Get("AlertMissedNext"), missed.Line, next.Line, Minutes(next.BusDeparture - now));
        return new AlertMessage(AlertKind.Missed, message, StopLine(missed), AlertUrgency.Normal);
    }

    /// <summary>"5 min walk + 1 min wait = 6 min before departure", then how long until it is time to go.</summary>
    private string WalkDetail(TripOption option, TimeSpan timeToLeave)
    {
        var walk = Math.Max(1, (int)Math.Round(option.WalkToStop.TotalMinutes));
        var wait = Math.Max(0, (int)Math.Round((option.BusDeparture - option.WalkToStop - option.LeaveBy).TotalMinutes));
        var leaveIn = Math.Max(0, (int)Math.Ceiling(timeToLeave.TotalMinutes));
        return string.Format(CultureInfo.CurrentCulture, localizer.Get("AlertWalkDetail"), walk, wait, walk + wait, leaveIn);
    }

    private string StopLine(TripOption option) =>
        string.Format(CultureInfo.CurrentCulture, localizer.Get("AlertAtStop"), option.OriginStop.Name);

    private static int Minutes(TimeSpan span) => Math.Max(1, (int)Math.Ceiling(span.TotalMinutes));
}
