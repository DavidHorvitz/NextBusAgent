namespace BusPulse.Services.Trips;

/// <summary>Turns a time of day chosen in the planner into an absolute instant, honouring daylight-saving changes.</summary>
public static class TripTime
{
    private const string IsraelZoneId = "Israel Standard Time";

    public static TimeZoneInfo IsraelZone { get; } = FindZone();

    /// <summary>
    /// Today at <paramref name="timeOfDay"/> (local to <paramref name="zone"/>), or tomorrow when that time has already passed.
    /// The offset is taken from the zone on that exact date, so a 12:00 target stays at 12:00 on a daylight-saving change day.
    /// A time skipped by a spring-forward change is moved one hour later.
    /// </summary>
    public static DateTimeOffset Resolve(DateTimeOffset now, TimeSpan timeOfDay, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        var candidate = local.Date + timeOfDay;
        if (candidate < local - TimeSpan.FromMinutes(1))
        {
            candidate = candidate.AddDays(1);
        }

        return ToInstant(candidate, zone);
    }

    private static DateTimeOffset ToInstant(DateTime wallClock, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        try
        {
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            // The wall-clock time does not exist on that day (spring-forward gap); use the first valid time after it.
            return ToInstant(wallClock.AddHours(1), zone);
        }
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
