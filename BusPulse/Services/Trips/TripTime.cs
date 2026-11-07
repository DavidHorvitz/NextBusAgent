namespace BusPulse.Services.Trips;

/// <summary>Turns a date and time of day chosen in the planner into an absolute instant, honouring daylight-saving changes.</summary>
public static class TripTime
{
    private const string IsraelZoneId = "Israel Standard Time";

    /// <summary>How many days ahead the planner lets the user pick a date (today included).</summary>
    public const int MaxDaysAhead = 60;

    public static TimeZoneInfo IsraelZone { get; } = FindZone();

    /// <summary>The calendar date of <paramref name="now"/> in <paramref name="zone"/>.</summary>
    public static DateOnly TodayIn(DateTimeOffset now, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);

    /// <summary>
    /// Today at <paramref name="timeOfDay"/> (local to <paramref name="zone"/>), or tomorrow when that time has already passed.
    /// The offset is taken from the zone on that exact date, so a 12:00 target stays at 12:00 on a daylight-saving change day.
    /// A time skipped by a spring-forward change is moved one hour later.
    /// </summary>
    public static DateTimeOffset Resolve(DateTimeOffset now, TimeSpan timeOfDay, TimeZoneInfo zone)
    {
        var today = TodayIn(now, zone);
        var date = HasPassed(now, today, timeOfDay, zone) ? today.AddDays(1) : today;
        return Resolve(date, timeOfDay, zone);
    }

    /// <summary>
    /// The instant of <paramref name="timeOfDay"/> on the explicit Israel-local calendar <paramref name="date"/>. Never rolls
    /// to another day: the caller decides what to do with a time that has passed. A time skipped by a spring-forward
    /// change is moved one hour later; an ambiguous autumn time takes its first occurrence (summer offset, the earlier instant), the same rule as GtfsTime.At.
    /// </summary>
    public static DateTimeOffset Resolve(DateOnly date, TimeSpan timeOfDay, TimeZoneInfo zone) =>
        ToInstant(date.ToDateTime(TimeOnly.MinValue) + timeOfDay, zone);

    /// <summary>True when the wall-clock time on <paramref name="date"/> is more than a minute in the past.</summary>
    public static bool HasPassed(DateTimeOffset now, DateOnly date, TimeSpan timeOfDay, TimeZoneInfo zone) =>
        Resolve(date, timeOfDay, zone) < now - TimeSpan.FromMinutes(1);

    private static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
        {
            // The wall-clock time does not exist on that day (spring-forward gap); use the first valid time after it.
            return ToInstant(wallClock.AddHours(1), zone);
        }

        // An ambiguous fall-back time occurs twice; take the first (summer offset, earlier instant), as GtfsTime.At does.
        var offset = zone.IsAmbiguousTime(unspecified) ? zone.GetAmbiguousTimeOffsets(unspecified).Max() : zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    private static TimeZoneInfo FindZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(IsraelZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
