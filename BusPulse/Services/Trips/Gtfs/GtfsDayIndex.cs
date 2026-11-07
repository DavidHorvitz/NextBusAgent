using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

[assembly: InternalsVisibleTo("BusPulse.Tests")]

namespace BusPulse.Services.Trips.Gtfs;

/// <summary>
/// Every stop call of every ride that runs on one Israeli calendar day, held compactly in memory. A ride that left the
/// previous evening and passes midnight is included with its after-midnight calls (times of 24:00:00 and later in the
/// previous service day). Rows carry seconds since midnight of <see cref="Date"/>, always 0..86399, so rides crossing
/// midnight appear in two day indexes with the same ride id and no overlap.
///
/// RideId scheme: FNV-1a 64-bit over the UTF-8 bytes of "trip_id|yyyyMMdd(service date)", with the sign bit cleared.
/// The service date is part of the key so the same trip_id running on two service days stays two rides. Two different
/// rides with the same id inside one index make the build fail (detected, not silently merged).
/// </summary>
internal sealed class GtfsDayIndex
{
    private const int StopKeyShift = 17; // seconds fit in 17 bits (< 131072)

    private readonly CompactRow[] _rows;
    private readonly int[] _rideStart;     // instance -> first row in _rows (rows grouped per instance, by sequence)
    private readonly int[] _byStop;        // row indexes ordered by (stop code, seconds)
    private readonly Dictionary<int, (int Start, int Length)> _stopRanges;
    private readonly Dictionary<long, int> _rides;
    private readonly long[] _rideIds;
    private readonly string[] _tripIds;
    private readonly RouteInfo[] _routeOfInstance;
    private readonly Dictionary<int, string> _stopNames;

    internal readonly record struct RouteInfo(string Line, string? Agency, string? LongName);

    /// <summary>The row kept once the index is assembled: 12 bytes, the ride is implied by its position.</summary>
    internal struct CompactRow
    {
        public int StopCode;
        public int Seconds;
        public ushort Sequence;
    }

    /// <summary>The 16-byte row used while building, before rows are grouped per ride.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Row
    {
        public int Instance;
        public int StopCode;
        public int Seconds;
        public ushort Sequence;
    }

    private GtfsDayIndex(
        DateOnly date, CompactRow[] rows, int[] rideStart, int[] byStop, Dictionary<int, (int, int)> stopRanges,
        Dictionary<long, int> rides, long[] rideIds, string[] tripIds, RouteInfo[] routes, Dictionary<int, string> stopNames)
    {
        Date = date;
        _rows = rows;
        _rideStart = rideStart;
        _byStop = byStop;
        _stopRanges = stopRanges;
        _rides = rides;
        _rideIds = rideIds;
        _tripIds = tripIds;
        _routeOfInstance = routes;
        _stopNames = stopNames;
    }

    public DateOnly Date { get; }

    public int RowCount => _rows.Length;

    public int RideCount => _rides.Count;

    /// <summary>Calls at one stop between two second offsets (inclusive), earliest first.</summary>
    public IEnumerable<RideStop> CallsAt(int stopCode, int fromSeconds, int toSeconds)
    {
        if (!_stopRanges.TryGetValue(stopCode, out var range) || toSeconds < fromSeconds)
        {
            yield break;
        }

        var lo = range.Start;
        var hi = range.Start + range.Length;
        // First position whose seconds >= fromSeconds.
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (_rows[_byStop[mid]].Seconds < fromSeconds)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (var i = lo; i < range.Start + range.Length; i++)
        {
            var row = _rows[_byStop[i]];
            if (row.Seconds > toSeconds)
            {
                yield break;
            }

            yield return ToRideStop(row, InstanceOf(_byStop[i]));
        }
    }

    /// <summary>The ride owning a row: rows are grouped per ride, so a binary search over the group starts finds it.</summary>
    private int InstanceOf(int rowIndex)
    {
        var lo = 0;
        var hi = _rideStart.Length - 2;
        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (_rideStart[mid] <= rowIndex)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    /// <summary>Calls of the given rides at the given stops, in ride then sequence order.</summary>
    public IEnumerable<RideStop> CallsOf(IReadOnlyCollection<long> rideIds, IReadOnlyCollection<long> stopCodes, int fromSeconds, int toSeconds)
    {
        var stops = stopCodes.ToHashSet();
        foreach (var rideId in rideIds.Distinct())
        {
            if (!_rides.TryGetValue(rideId, out var instance))
            {
                continue;
            }

            for (var i = _rideStart[instance]; i < _rideStart[instance + 1]; i++)
            {
                var row = _rows[i];
                if (row.Seconds >= fromSeconds && row.Seconds <= toSeconds && stops.Contains(row.StopCode))
                {
                    yield return ToRideStop(row, instance);
                }
            }
        }
    }

    private RideStop ToRideStop(CompactRow row, int instance)
    {
        var route = _routeOfInstance[instance];
        return new RideStop(
            _rideIds[instance],
            row.StopCode,
            row.Sequence,
            GtfsTime.At(Date, row.Seconds),
            route.Line,
            route.Agency,
            route.LongName,
            _stopNames.GetValueOrDefault(row.StopCode),
            row.StopCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _tripIds[instance]);
    }

    public static long ComputeRideId(string tripId, DateOnly serviceDate)
    {
        var bytes = Encoding.UTF8.GetBytes(tripId + "|" + serviceDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        var hash = 14695981039346656037UL;
        foreach (var b in bytes)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return (long)(hash & long.MaxValue);
    }

    // ---- Building ----

    private const long MaxStopTimesBytes = 1_500_000_000;
    private const long MaxTripsBytes = 300_000_000;
    private const long MaxSmallBytes = 100_000_000;
    private const int MaxRows = 25_000_000;

    /// <summary>Builds the index for one date from the feed zip. Two streaming passes; nothing is extracted to disk.</summary>
    public static GtfsDayIndex Build(string zipPath, DateOnly date, GtfsServiceCalendar calendar)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);
        string Intern(string value) => pool.TryGetValue(value, out var existing) ? existing : pool[value] = value;

        // agencies
        var agencies = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = Open(zip, "agency.txt", MaxSmallBytes, out var splitter, out var header))
        {
            var id = header.Find("agency_id");
            var name = header.Require("agency_name", "agency.txt");
            while (reader.TryRead(out var line))
            {
                splitter.Split(line);
                var key = id >= 0 ? splitter.Text(line, id, 64) : string.Empty;
                agencies[key] = Intern(splitter.Text(line, name));
            }
        }

        // routes
        var routeIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var routeList = new List<RouteInfo>();
        using (var reader = Open(zip, "routes.txt", MaxSmallBytes, out var splitter, out var header))
        {
            var routeId = header.Require("route_id", "routes.txt");
            var agency = header.Find("agency_id");
            var shortName = header.Require("route_short_name", "routes.txt");
            var longName = header.Find("route_long_name");
            while (reader.TryRead(out var line))
            {
                splitter.Split(line);
                var id = splitter.Text(line, routeId, 64);
                if (id.Length == 0)
                {
                    continue;
                }

                var agencyName = agency >= 0 && agencies.TryGetValue(splitter.Text(line, agency, 64), out var a)
                    ? a
                    : agencies.Count == 1 ? agencies.Values.First() : null;
                routeIds[id] = routeList.Count;
                routeList.Add(new RouteInfo(
                    Intern(splitter.Text(line, shortName)),
                    agencyName,
                    longName >= 0 ? Intern(splitter.Text(line, longName)) : null));
            }
        }

        // stops: stop_id -> stop_code (numeric only), names by code
        var stopCodeById = new Dictionary<int, int>();
        var stopNames = new Dictionary<int, string>();
        using (var reader = Open(zip, "stops.txt", MaxSmallBytes, out var splitter, out var header))
        {
            var stopId = header.Require("stop_id", "stops.txt");
            var stopCode = header.Require("stop_code", "stops.txt");
            var stopName = header.Find("stop_name");
            while (reader.TryRead(out var line))
            {
                splitter.Split(line);
                if (!GtfsCsvSplitter.TryInt(splitter.Raw(line, stopId), out var id) ||
                    !GtfsCsvSplitter.TryInt(splitter.Raw(line, stopCode), out var code))
                {
                    continue;
                }

                stopCodeById[id] = code;
                if (stopName >= 0)
                {
                    stopNames[code] = splitter.Text(line, stopName);
                }
            }
        }

        // trips active on the date or (for after-midnight calls) on the day before
        var previous = date.AddDays(-1);
        var tripIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var tripIds = new List<string>();
        var tripRoute = new List<RouteInfo>();
        var instanceOfToday = new List<int>();
        var instanceOfPrevious = new List<int>();
        var instances = new List<(int Trip, DateOnly ServiceDate)>();
        using (var reader = Open(zip, "trips.txt", MaxTripsBytes, out var splitter, out var header))
        {
            var routeCol = header.Require("route_id", "trips.txt");
            var serviceCol = header.Require("service_id", "trips.txt");
            var tripCol = header.Require("trip_id", "trips.txt");
            while (reader.TryRead(out var line))
            {
                splitter.Split(line);
                if (splitter.Count <= Math.Max(routeCol, Math.Max(serviceCol, tripCol)))
                {
                    continue;
                }

                var service = splitter.Text(line, serviceCol, 64);
                var today = calendar.IsActive(service, date);
                var yesterday = calendar.IsActive(service, previous);
                if (!today && !yesterday)
                {
                    continue;
                }

                var tripId = splitter.Text(line, tripCol, 96);
                if (tripId.Length == 0 || !routeIds.TryGetValue(splitter.Text(line, routeCol, 64), out var route) || tripIndex.ContainsKey(tripId))
                {
                    continue;
                }

                tripIndex[tripId] = tripIds.Count;
                tripIds.Add(tripId);
                tripRoute.Add(routeList[route]);
                instanceOfToday.Add(today ? NewInstance(instances, tripIds.Count - 1, date) : -1);
                instanceOfPrevious.Add(yesterday ? NewInstance(instances, tripIds.Count - 1, previous) : -1);
            }
        }

        // stop_times: one pass, compact rows only for the instances above
        var rows = new Row[1 << 21];
        var count = 0;
        var lookup = tripIndex.GetAlternateLookup<ReadOnlySpan<char>>();
        using (var reader = Open(zip, "stop_times.txt", MaxStopTimesBytes, out var splitter, out var header))
        {
            var tripCol = header.Require("trip_id", "stop_times.txt");
            var arrivalCol = header.Find("arrival_time");
            var departureCol = header.Find("departure_time");
            var stopCol = header.Require("stop_id", "stop_times.txt");
            var sequenceCol = header.Require("stop_sequence", "stop_times.txt");
            if (arrivalCol < 0 && departureCol < 0)
            {
                throw new InvalidDataException("stop_times.txt has no arrival_time or departure_time column.");
            }

            var lastTrip = new char[128];
            var lastTripLength = -1;
            var lastTripIndex = -1;
            while (reader.TryRead(out var line))
            {
                splitter.Split(line);
                var needed = Math.Max(Math.Max(tripCol, stopCol), Math.Max(sequenceCol, Math.Max(arrivalCol, departureCol)));
                if (splitter.Count <= needed)
                {
                    continue;
                }

                var trip = splitter.Raw(line, tripCol);
                if (lastTripLength != trip.Length || !trip.SequenceEqual(lastTrip.AsSpan(0, lastTripLength)))
                {
                    lastTripIndex = trip.Length <= lastTrip.Length && lookup.TryGetValue(trip, out var found) ? found : -1;
                    if (trip.Length <= lastTrip.Length)
                    {
                        trip.CopyTo(lastTrip);
                        lastTripLength = trip.Length;
                    }
                    else
                    {
                        lastTripLength = -1;
                    }
                }

                if (lastTripIndex < 0)
                {
                    continue;
                }

                var seconds = 0;
                if (!(arrivalCol >= 0 && GtfsTime.TryParse(splitter.Raw(line, arrivalCol), out seconds)) &&
                    !(departureCol >= 0 && GtfsTime.TryParse(splitter.Raw(line, departureCol), out seconds)))
                {
                    continue;
                }

                int instance;
                if (seconds < GtfsTime.SecondsPerDay)
                {
                    instance = instanceOfToday[lastTripIndex];
                }
                else if (seconds < GtfsTime.MaxSeconds)
                {
                    instance = instanceOfPrevious[lastTripIndex];
                    seconds -= GtfsTime.SecondsPerDay;
                }
                else
                {
                    continue;
                }

                if (instance < 0 ||
                    !GtfsCsvSplitter.TryInt(splitter.Raw(line, stopCol), out var stopId) || !stopCodeById.TryGetValue(stopId, out var stopCodeValue) ||
                    !GtfsCsvSplitter.TryInt(splitter.Raw(line, sequenceCol), out var sequence))
                {
                    continue;
                }

                if (count == MaxRows)
                {
                    throw new InvalidDataException("The feed has more stop calls for one day than the allowed maximum.");
                }

                if (count == rows.Length)
                {
                    Array.Resize(ref rows, rows.Length + rows.Length / 2);
                }

                rows[count++] = new Row
                {
                    Instance = instance,
                    StopCode = stopCodeValue,
                    Seconds = seconds,
                    Sequence = (ushort)Math.Min(sequence, ushort.MaxValue)
                };
            }
        }

        Array.Resize(ref rows, count);
        return Assemble(date, rows, instances, tripIds, tripRoute, stopNames);
    }

    private static int NewInstance(List<(int, DateOnly)> instances, int trip, DateOnly serviceDate)
    {
        instances.Add((trip, serviceDate));
        return instances.Count - 1;
    }

    /// <summary>Groups rows per ride, orders them for stop queries and drops instances without rows.</summary>
    internal static GtfsDayIndex Assemble(
        DateOnly date, Row[] rows, List<(int Trip, DateOnly ServiceDate)> instances, List<string> tripIds,
        List<RouteInfo> tripRoute, Dictionary<int, string> stopNames)
    {
        // Renumber: keep only instances that have rows.
        var remap = new int[instances.Count];
        Array.Fill(remap, -1);
        var kept = 0;
        foreach (var row in rows)
        {
            if (remap[row.Instance] < 0)
            {
                remap[row.Instance] = 0;
            }
        }

        for (var i = 0; i < remap.Length; i++)
        {
            if (remap[i] == 0)
            {
                remap[i] = kept++;
            }
        }

        var ids = new long[kept];
        var rideTrips = new string[kept];
        var routes = new RouteInfo[kept];
        var rides = new Dictionary<long, int>(kept);
        for (var i = 0; i < remap.Length; i++)
        {
            if (remap[i] < 0)
            {
                continue;
            }

            var (trip, serviceDate) = instances[i];
            var id = ComputeRideId(tripIds[trip], serviceDate);
            if (!rides.TryAdd(id, remap[i]))
            {
                throw new InvalidDataException("Two different rides of the feed share one 64-bit ride id.");
            }

            ids[remap[i]] = id;
            rideTrips[remap[i]] = tripIds[trip];
            routes[remap[i]] = tripRoute[trip];
        }

        for (var i = 0; i < rows.Length; i++)
        {
            rows[i].Instance = remap[rows[i].Instance];
        }

        if (!IsGrouped(rows))
        {
            Array.Sort(rows, static (a, b) =>
            {
                var byInstance = a.Instance.CompareTo(b.Instance);
                return byInstance != 0 ? byInstance : a.Sequence.CompareTo(b.Sequence);
            });
        }

        var rideStart = new int[kept + 1];
        var cursor = 0;
        for (var instance = 0; instance < kept; instance++)
        {
            rideStart[instance] = cursor;
            while (cursor < rows.Length && rows[cursor].Instance == instance)
            {
                cursor++;
            }
        }

        rideStart[kept] = rows.Length;

        var compact = new CompactRow[rows.Length];
        var keys = new long[rows.Length];
        var byStop = new int[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            compact[i] = new CompactRow { StopCode = rows[i].StopCode, Seconds = rows[i].Seconds, Sequence = rows[i].Sequence };
            keys[i] = ((long)rows[i].StopCode << StopKeyShift) | (uint)rows[i].Seconds;
            byStop[i] = i;
        }

        Array.Sort(keys, byStop);
        var ranges = new Dictionary<int, (int, int)>();
        var runStart = 0;
        for (var i = 1; i <= keys.Length; i++)
        {
            if (i == keys.Length || keys[i] >> StopKeyShift != keys[runStart] >> StopKeyShift)
            {
                ranges[(int)(keys[runStart] >> StopKeyShift)] = (runStart, i - runStart);
                runStart = i;
            }
        }

        return new GtfsDayIndex(date, compact, rideStart, byStop, ranges, rides, ids, rideTrips, routes, stopNames);
    }

    private static bool IsGrouped(Row[] rows)
    {
        for (var i = 1; i < rows.Length; i++)
        {
            if (rows[i].Instance < rows[i - 1].Instance ||
                rows[i].Instance == rows[i - 1].Instance && rows[i].Sequence < rows[i - 1].Sequence)
            {
                return false;
            }
        }

        return true;
    }

    private static GtfsLineReader Open(ZipArchive zip, string name, long maxBytes, out GtfsCsvSplitter splitter, out GtfsHeader header)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"The feed has no {name}.");
        var reader = new GtfsLineReader(new GtfsLimitedStream(entry.Open(), maxBytes, name));
        splitter = new GtfsCsvSplitter();
        header = GtfsHeader.Read(reader, splitter, name);
        return reader;
    }
}
