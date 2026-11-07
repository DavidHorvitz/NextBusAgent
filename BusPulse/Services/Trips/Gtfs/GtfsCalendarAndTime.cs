using System.Globalization;
using System.IO;

namespace BusPulse.Services.Trips.Gtfs;

/// <summary>
/// Which service ids run on which date: calendar.txt (weekday flags plus a start/end range) with calendar_dates.txt
/// exceptions on top (type 1 adds a date, type 2 removes it). Either file may be missing.
/// </summary>
internal sealed class GtfsServiceCalendar
{
    private readonly record struct Weekly(byte Days, int Start, int End);

    private readonly Dictionary<string, Weekly> _weekly = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _added = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<int>> _removed = new(StringComparer.Ordinal);
    private int _first = int.MaxValue;
    private int _last = int.MinValue;

    /// <summary>First and last date for which the feed lists any service, or null when it lists none.</summary>
    public (DateOnly First, DateOnly Last)? Range =>
        _first <= _last && TryDate(_first, out var first) && TryDate(_last, out var last) ? (first, last) : null;

    public static string[] DayColumns { get; } = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    public bool IsActive(string serviceId, DateOnly date)
    {
        var key = Key(date);
        if (_removed.TryGetValue(serviceId, out var removed) && removed.Contains(key))
        {
            return false;
        }

        if (_added.TryGetValue(serviceId, out var added) && added.Contains(key))
        {
            return true;
        }

        return _weekly.TryGetValue(serviceId, out var weekly) &&
               key >= weekly.Start && key <= weekly.End &&
               (weekly.Days & (1 << (int)date.DayOfWeek)) != 0;
    }

    /// <summary>Reads both files; a null stream means the file is absent. Rows that cannot be parsed are skipped.</summary>
    public static GtfsServiceCalendar Load(Stream? calendar, Stream? calendarDates)
    {
        var result = new GtfsServiceCalendar();
        if (calendar is not null)
        {
            result.ReadCalendar(calendar);
        }

        if (calendarDates is not null)
        {
            result.ReadCalendarDates(calendarDates);
        }

        return result;
    }

    private void ReadCalendar(Stream stream)
    {
        using var reader = new GtfsLineReader(stream);
        var splitter = new GtfsCsvSplitter();
        var header = GtfsHeader.Read(reader, splitter, "calendar.txt");
        var service = header.Require("service_id", "calendar.txt");
        var start = header.Require("start_date", "calendar.txt");
        var end = header.Require("end_date", "calendar.txt");
        var days = DayColumns.Select(header.Find).ToArray();
        while (reader.TryRead(out var line))
        {
            splitter.Split(line);
            if (splitter.Count <= Math.Max(service, Math.Max(start, end)) ||
                !GtfsCsvSplitter.TryInt(splitter.Raw(line, start), out var startDate) ||
                !GtfsCsvSplitter.TryInt(splitter.Raw(line, end), out var endDate) ||
                !TryDate(startDate, out _) || !TryDate(endDate, out _) || endDate < startDate)
            {
                continue;
            }

            var id = splitter.Text(line, service, 64);
            if (id.Length == 0)
            {
                continue;
            }

            byte mask = 0;
            for (var d = 0; d < 7; d++)
            {
                if (days[d] >= 0 && splitter.Raw(line, days[d]).Trim() is "1")
                {
                    mask |= (byte)(1 << d);
                }
            }

            _weekly[id] = new Weekly(mask, startDate, endDate);
            if (mask != 0)
            {
                _first = Math.Min(_first, startDate);
                _last = Math.Max(_last, endDate);
            }
        }
    }

    private void ReadCalendarDates(Stream stream)
    {
        using var reader = new GtfsLineReader(stream);
        var splitter = new GtfsCsvSplitter();
        var header = GtfsHeader.Read(reader, splitter, "calendar_dates.txt");
        var service = header.Require("service_id", "calendar_dates.txt");
        var date = header.Require("date", "calendar_dates.txt");
        var type = header.Require("exception_type", "calendar_dates.txt");
        while (reader.TryRead(out var line))
        {
            splitter.Split(line);
            if (splitter.Count <= Math.Max(service, Math.Max(date, type)) ||
                !GtfsCsvSplitter.TryInt(splitter.Raw(line, date), out var key) || !TryDate(key, out _) ||
                !GtfsCsvSplitter.TryInt(splitter.Raw(line, type), out var kind) || kind is not (1 or 2))
            {
                continue;
            }

            var id = splitter.Text(line, service, 64);
            var target = kind == 1 ? _added : _removed;
            if (!target.TryGetValue(id, out var set))
            {
                target[id] = set = [];
            }

            set.Add(key);
            if (kind == 1)
            {
                _first = Math.Min(_first, key);
                _last = Math.Max(_last, key);
            }
        }
    }

    private static int Key(DateOnly date) => date.Year * 10000 + date.Month * 100 + date.Day;

    private static bool TryDate(int key, out DateOnly date)
    {
        date = default;
        int year = key / 10000, month = key / 100 % 100, day = key % 100;
        if (year is < 2000 or > 2200 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }
}

/// <summary>
/// GTFS times are wall-clock times in Israel on the service day and can pass 24:00:00 for trips that run after midnight.
/// They are converted as wall-clock (service-day midnight plus the seconds), not as elapsed time, because that is what a
/// bus sign shows. On the spring-forward day the skipped hour (02:00-02:59) resolves to the hour after it; on the
/// fall-back day the repeated hour (01:00-01:59) resolves to its first occurrence (summer offset).
/// </summary>
internal static class GtfsTime
{
    public const int SecondsPerDay = 86_400;
    public const int MaxSeconds = 2 * SecondsPerDay;

    public static TimeZoneInfo Israel { get; } = FindZone();

    /// <summary>Parses H:MM:SS or HH:MM:SS (hours up to 47); returns seconds since service-day midnight.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out int seconds)
    {
        seconds = 0;
        text = text.Trim();
        var first = text.IndexOf(':');
        if (first < 1 || first > 2)
        {
            return false;
        }

        var rest = text[(first + 1)..];
        var second = rest.IndexOf(':');
        if (second != 2 || rest.Length != 5)
        {
            return false;
        }

        if (!int.TryParse(text[..first], NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(rest[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ||
            !int.TryParse(rest[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ||
            h > 47 || m > 59 || s > 59)
        {
            return false;
        }

        seconds = h * 3600 + m * 60 + s;
        return true;
    }

    /// <summary>The instant of <paramref name="seconds"/> after midnight of <paramref name="date"/> on the Israeli wall clock.</summary>
    public static DateTimeOffset At(DateOnly date, int seconds)
    {
        var local = date.ToDateTime(TimeOnly.MinValue).AddSeconds(seconds);
        TimeSpan offset;
        if (Israel.IsInvalidTime(local))
        {
            local = local.AddHours(1);
            offset = Israel.GetUtcOffset(local);
        }
        else if (Israel.IsAmbiguousTime(local))
        {
            offset = Israel.GetAmbiguousTimeOffsets(local).Max();
        }
        else
        {
            offset = Israel.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset);
    }

    public static DateOnly LocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Israel).DateTime);

    /// <summary>Wall-clock seconds since midnight of <paramref name="date"/> (may be negative or above a day).</summary>
    public static double WallSeconds(DateOnly date, DateTimeOffset instant) =>
        (TimeZoneInfo.ConvertTime(instant, Israel).DateTime - date.ToDateTime(TimeOnly.MinValue)).TotalSeconds;

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Asia/Jerusalem", "Israel Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
        }

        throw new InvalidOperationException("The Israel time zone is not available on this machine.");
    }
}
