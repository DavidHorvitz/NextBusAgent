using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using BusPulse.Services.Trips;
using BusPulse.Services.Trips.Gtfs;
using Xunit;

namespace BusPulse.Tests;

public sealed class GtfsCalendarAndTimeTests
{
    private const string CalendarHeader = "service_id,sunday,monday,tuesday,wednesday,thursday,friday,saturday,start_date,end_date\r\n";

    private static GtfsServiceCalendar Load(string? calendar, string? dates = null) =>
        GtfsServiceCalendar.Load(
            calendar is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(calendar)),
            dates is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(dates)));

    [Fact]
    public void WeeklyFlagsAndRange_DecideTheService()
    {
        var calendar = Load(CalendarHeader + "S,1,1,0,0,0,0,0,20261101,20261130\r\n");

        Assert.True(calendar.IsActive("S", new DateOnly(2026, 11, 8)));    // Sunday
        Assert.True(calendar.IsActive("S", new DateOnly(2026, 11, 9)));    // Monday
        Assert.False(calendar.IsActive("S", new DateOnly(2026, 11, 10)));  // Tuesday
        Assert.False(calendar.IsActive("S", new DateOnly(2026, 12, 6)));   // Sunday, after the end
        Assert.False(calendar.IsActive("Other", new DateOnly(2026, 11, 8)));
    }

    [Fact]
    public void StartAndEndDates_AreInclusive()
    {
        var calendar = Load(CalendarHeader + "S,1,1,1,1,1,1,1,20261108,20261110\r\n");

        Assert.False(calendar.IsActive("S", new DateOnly(2026, 11, 7)));
        Assert.True(calendar.IsActive("S", new DateOnly(2026, 11, 8)));
        Assert.True(calendar.IsActive("S", new DateOnly(2026, 11, 10)));
        Assert.False(calendar.IsActive("S", new DateOnly(2026, 11, 11)));
    }

    [Fact]
    public void Exceptions_RemoveAndAddDates_AndExtendTheRange()
    {
        var calendar = Load(
            CalendarHeader + "S,1,0,0,0,0,0,0,20261101,20261130\r\n",
            "service_id,date,exception_type\r\nS,20261108,2\r\nS,20261111,1\r\nS,20261214,1\r\nS,20261115,9\r\n");

        Assert.False(calendar.IsActive("S", new DateOnly(2026, 11, 8)));   // removed Sunday
        Assert.True(calendar.IsActive("S", new DateOnly(2026, 11, 15)));   // an unknown exception type is ignored: normal Sunday
        Assert.True(calendar.IsActive("S", new DateOnly(2026, 11, 11)));   // added Wednesday
        Assert.True(calendar.IsActive("S", new DateOnly(2026, 12, 14)));   // added outside the range
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 14)), calendar.Range);
    }

    [Fact]
    public void MissingCalendarFile_StillWorksFromExceptionsAlone()
    {
        var calendar = Load(null, "service_id,date,exception_type\r\nS,20261108,1\r\n");

        Assert.True(calendar.IsActive("S", new DateOnly(2026, 11, 8)));
        Assert.Equal((new DateOnly(2026, 11, 8), new DateOnly(2026, 11, 8)), calendar.Range);
    }

    [Theory]
    [InlineData("05:10:00", 18_600)]
    [InlineData("5:10:00", 18_600)]
    [InlineData("24:00:00", 86_400)]
    [InlineData("25:30:15", 91_815)]
    public void TimesParse_IncludingPastTwentyFourHours(string text, int seconds)
    {
        Assert.True(GtfsTime.TryParse(text, out var parsed));
        Assert.Equal(seconds, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12:60:00")]
    [InlineData("12:00:60")]
    [InlineData("99:00:00")]
    [InlineData("12:00")]
    public void MalformedTimes_AreRejected(string text) => Assert.False(GtfsTime.TryParse(text, out _));

    [Fact]
    public void WinterAndSummerTimes_GetTheirOwnOffset()
    {
        Assert.Equal(TimeSpan.FromHours(2), GtfsTime.At(new DateOnly(2026, 11, 8), 8 * 3600).Offset);
        Assert.Equal(TimeSpan.FromHours(3), GtfsTime.At(new DateOnly(2026, 7, 8), 8 * 3600).Offset);
    }

    [Fact]
    public void TimesPastMidnight_RollIntoTheNextCalendarDay()
    {
        var time = GtfsTime.At(new DateOnly(2026, 11, 7), 25 * 3600 + 10 * 60);

        Assert.Equal(new DateTimeOffset(2026, 11, 8, 1, 10, 0, TimeSpan.FromHours(2)), time);
    }

    [Fact]
    public void FallBackDay_2026_10_25_UsesSummerOffsetBeforeAndWinterOffsetAfter()
    {
        var date = new DateOnly(2026, 10, 25);

        Assert.Equal(TimeSpan.FromHours(3), GtfsTime.At(date, 30 * 60).Offset);          // 00:30
        Assert.Equal(TimeSpan.FromHours(2), GtfsTime.At(date, 5 * 3600).Offset);         // 05:00
        // The repeated hour resolves to its first occurrence.
        Assert.Equal(TimeSpan.FromHours(3), GtfsTime.At(date, 3600 + 30 * 60).Offset);   // 01:30
        // A trip of the previous service day: 25:10 is 01:10 on the 25th.
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 10, 0, TimeSpan.FromHours(3)), GtfsTime.At(new DateOnly(2026, 10, 24), 25 * 3600 + 600));
        // The whole day is 25 hours long: 23:00 to midnight is winter time.
        Assert.Equal(TimeSpan.FromHours(2), GtfsTime.At(date, 23 * 3600).Offset);
    }

    [Fact]
    public void SpringForwardDay_2027_03_26_SkipsTheMissingHour()
    {
        var date = new DateOnly(2027, 3, 26);

        Assert.Equal(TimeSpan.FromHours(2), GtfsTime.At(date, 3600 + 30 * 60).Offset);   // 01:30 is winter time
        Assert.Equal(TimeSpan.FromHours(3), GtfsTime.At(date, 5 * 3600).Offset);         // 05:00 is summer time
        // 02:30 does not exist; it resolves to 03:30 summer time.
        Assert.Equal(new DateTimeOffset(2027, 3, 26, 3, 30, 0, TimeSpan.FromHours(3)), GtfsTime.At(date, 2 * 3600 + 30 * 60));
        // 26:00 of the 25th is 02:00 on the 26th, also inside the gap.
        Assert.Equal(new DateTimeOffset(2027, 3, 26, 3, 0, 0, TimeSpan.FromHours(3)), GtfsTime.At(new DateOnly(2027, 3, 25), 26 * 3600));
    }
}

public sealed class GtfsCsvTests
{
    [Fact]
    public void QuotedFields_KeepCommasAndDoubledQuotes()
    {
        var splitter = new GtfsCsvSplitter();
        const string line = "1,\"Dizengoff, \"\"North\"\" side\",,x";

        splitter.Split(line);

        Assert.Equal(4, splitter.Count);
        Assert.Equal("1", splitter.Text(line, 0));
        Assert.Equal("Dizengoff, \"North\" side", splitter.Text(line, 1));
        Assert.Equal(string.Empty, splitter.Text(line, 2));
        Assert.Equal("x", splitter.Text(line, 3));
    }

    [Fact]
    public void UnterminatedQuote_DoesNotThrow()
    {
        var splitter = new GtfsCsvSplitter();
        const string line = "1,\"never closed,3";

        splitter.Split(line);

        Assert.Equal(2, splitter.Count);
        Assert.Equal("never closed,3", splitter.Text(line, 1));
    }

    [Fact]
    public void LineReader_HandlesCrLfBomAndSkipsOverlongLines()
    {
        var text = "﻿a,b\r\n" + new string('x', 5_000) + "\r\nc,d\r\ne,f";
        using var reader = new GtfsLineReader(new MemoryStream(Encoding.UTF8.GetBytes(text)), maxLineLength: 1_000);

        var lines = new List<string>();
        while (reader.TryRead(out var line))
        {
            lines.Add(line.ToString());
        }

        Assert.Equal(new[] { "a,b", "c,d", "e,f" }, lines);
        Assert.Equal(1, reader.SkippedLines);
    }

    [Fact]
    public void LimitedStream_FailsOnceTheCapIsPassed()
    {
        using var stream = new GtfsLimitedStream(new MemoryStream(new byte[10_000]), 4_096, "stop_times.txt");
        var buffer = new byte[1_000];

        var exception = Assert.Throws<InvalidDataException>(() =>
        {
            while (stream.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        });

        Assert.Contains("stop_times.txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LineReader_OverTheCap_NeverBuffersTheWholeLine()
    {
        // 3 MB without a newline then a good line: memory stays bounded and the good line still arrives.
        var payload = new string('y', 3_000_000) + "\nok,1\n";
        using var reader = new GtfsLineReader(new MemoryStream(Encoding.UTF8.GetBytes(payload)), maxLineLength: 1_000);

        Assert.True(reader.TryRead(out var line));
        Assert.Equal("ok,1", line.ToString());
        Assert.Equal(1, reader.SkippedLines);
    }
}

public sealed class GtfsFeedScheduleSourceTests
{
    private static readonly DateOnly Sunday = new(2026, 11, 8);
    private static readonly TimeSpan Winter = TimeSpan.FromHours(2);

    private static DateTimeOffset At(int day, int hour, int minute = 0, int second = 0) =>
        new(2026, 11, day, hour, minute, second, Winter);

    /// <summary>A source whose cache holds the real-subset fixture, cut on 2026-11-07, so no request is ever made.</summary>
    private static (GtfsFeedScheduleSource Source, GtfsScratch Scratch, FixedClock Clock) FixtureSource()
    {
        var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        scratch.PlaceCached(GtfsScratch.FixtureBytes(), clock.UtcNow, TimeSpan.FromHours(1));
        return (scratch.Source(StubFactory.NoNetwork(), clock), scratch, clock);
    }

    [Fact]
    public async Task Departures_ComeBackInOrderWithTheFeedFields()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;

        var rows = await source.GetStopDeparturesAsync(3490, At(8, 7), At(8, 8));

        Assert.Equal(new[] { "458670_081126", "56109271_081126" }, rows.Select(row => row.JourneyRef).ToArray());
        var first = rows[0];
        Assert.Equal(At(8, 7, 3, 59), first.Time);
        Assert.Equal(3490, first.StopId);
        Assert.Equal("3490", first.StopCode);
        Assert.Equal(6, first.Sequence);
        Assert.Equal("3", first.Line);
        Assert.Equal("דן", first.Agency);
        Assert.Equal("תחנת שיטור/איילת השחר", first.StopName);
        Assert.Contains("בני ברק", first.RouteLongName, StringComparison.Ordinal);
        Assert.Equal(At(8, 7, 18, 59), rows[1].Time);
    }

    [Fact]
    public async Task Departures_OutsideTheWindowAreLeftOut()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;

        var rows = await source.GetStopDeparturesAsync(3490, At(8, 7, 4), At(8, 7, 18));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ServiceException_RemovesARideOnItsDate()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;

        var rows = await source.GetStopDeparturesAsync(3490, At(8, 0), At(8, 23, 59));

        // 53312930_081126 (07:11:56, line 9) belongs to a service the fixture removes on 2026-11-08.
        Assert.DoesNotContain(rows, row => row.JourneyRef == "53312930_081126");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task ServiceException_AddsARideOnAnOtherwiseEmptyDate()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;

        var rows = await source.GetStopDeparturesAsync(3490, At(15, 0), At(15, 23, 59));

        Assert.Equal(new[] { "458670_081126", "56109271_081126" }, rows.Select(row => row.JourneyRef).ToArray());
        Assert.Equal(At(15, 7, 3, 59), rows[0].Time);
    }

    [Fact]
    public async Task AfterMidnightCalls_OfThePreviousServiceDay_LandOnTheNextCalendarDay()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;

        var onMonday = await source.GetStopDeparturesAsync(20087, At(9, 0), At(9, 1));
        var onSunday = await source.GetStopDeparturesAsync(20087, At(8, 0), At(8, 23, 59));

        var ride = Assert.Single(onMonday);
        Assert.Equal("10508041_081126", ride.JourneyRef);   // a Sunday trip
        Assert.Equal(At(9, 0, 0, 59), ride.Time);            // 24:00:59 of the Sunday service day
        Assert.Equal(32, ride.Sequence);
        Assert.Empty(onSunday);
    }

    [Fact]
    public async Task WindowAcrossMidnight_ReadsBothDays_WithoutDuplicates()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;

        var rows = await source.GetStopDeparturesAsync(20087, At(8, 23), At(9, 1));

        Assert.Single(rows);
    }

    [Fact]
    public async Task RidesAtStops_KeepSequenceOrderWithinEachRide()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;
        var departures = await source.GetStopDeparturesAsync(3490, At(8, 7), At(8, 8));
        var rideIds = departures.Select(row => row.RideId).ToList();

        var rows = await source.GetRideStopsAtAsync(rideIds, [3490, 2656, 3128], At(8, 7), At(8, 8));

        Assert.Equal(6, rows.Count);
        foreach (var rideId in rideIds)
        {
            var sequence = rows.Where(row => row.RideId == rideId).Select(row => row.Sequence).ToArray();
            Assert.Equal(new[] { 6, 7, 8 }, sequence);
        }

        Assert.Equal(rows.Select(row => row.Time).OrderBy(time => time), rows.Select(row => row.Time));
    }

    [Fact]
    public async Task RidesAtStops_OnlyReturnTheRequestedStopsAndRides()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;
        var departures = await source.GetStopDeparturesAsync(3490, At(8, 7), At(8, 8));

        var rows = await source.GetRideStopsAtAsync([departures[0].RideId, 12345L], [2656], At(8, 7), At(8, 8));

        var row = Assert.Single(rows);
        Assert.Equal(2656, row.StopId);
        Assert.Equal(departures[0].RideId, row.RideId);
    }

    [Fact]
    public async Task RideIds_AreStableDistinctAndDocumented()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;

        var rows = await source.GetStopDeparturesAsync(3490, At(8, 7), At(8, 8));
        var again = await source.GetRideStopsAtAsync([rows[0].RideId], [2656], At(8, 7), At(8, 8));

        Assert.NotEqual(rows[0].RideId, rows[1].RideId);
        Assert.Equal(rows[0].RideId, again[0].RideId);
        Assert.Equal(GtfsDayIndex.ComputeRideId("458670_081126", Sunday), rows[0].RideId);
        Assert.True(rows[0].RideId > 0);
    }

    [Fact]
    public async Task GetRideStopsAsync_IsEmptyBecauseTheLinesTabIsTodayOnly()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;

        Assert.Empty(await source.GetRideStopsAsync(1, At(8, 0), At(8, 23)));
    }

    [Fact]
    public async Task Prepare_ReportsReady_OutsideRange_AndNoServiceDays()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;
        var messages = new List<string>();

        var ready = await source.PrepareAsync(Sunday, new Progress<string>(messages.Add), default);
        var after = await source.PrepareAsync(new DateOnly(2026, 11, 20), null, default);
        var before = await source.PrepareAsync(new DateOnly(2026, 11, 7), null, default);
        var gap = await source.PrepareAsync(new DateOnly(2026, 11, 10), null, default);

        Assert.Equal(FeedState.Ready, ready.State);
        Assert.Equal(new DateOnly(2026, 11, 8), ready.FirstDate);
        Assert.Equal(new DateOnly(2026, 11, 15), ready.LastDate);
        Assert.Equal(FeedState.DateOutsideFeed, after.State);
        Assert.Equal(new DateOnly(2026, 11, 15), after.LastDate);
        Assert.Equal(FeedState.DateOutsideFeed, before.State);
        Assert.Equal(FeedState.NoServiceOnDate, gap.State);
    }

    [Fact]
    public async Task Queries_ForADateOutsideTheFeed_ReturnNothing()
    {
        var (source, scratch, _) = FixtureSource();
        using var _ = scratch;

        Assert.Empty(await source.GetStopDeparturesAsync(3490, At(8, 7).AddDays(30), At(8, 8).AddDays(30)));
        Assert.Empty(await source.GetRideStopsAtAsync([1], [3490], At(8, 7).AddDays(30), At(8, 8).AddDays(30)));
    }

    [Fact]
    public async Task Departures_HonourLimitAndLatestFirst()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        var trips = new StringBuilder("route_id,service_id,trip_id\r\n");
        var times = new StringBuilder("trip_id,arrival_time,departure_time,stop_id,stop_sequence\r\n");
        for (var i = 0; i < 150; i++)
        {
            trips.Append(CultureInvariant($"10,S,T{i}\r\n"));
            var seconds = 6 * 3600 + i * 60;
            var text = CultureInvariant($"{seconds / 3600:00}:{seconds / 60 % 60:00}:00");
            times.Append(CultureInvariant($"T{i},{text},{text},100,1\r\n"));
        }

        scratch.PlaceCached(GtfsScratch.MiniFeed(
            "service_id,sunday,monday,tuesday,wednesday,thursday,friday,saturday,start_date,end_date\r\nS,1,0,0,0,0,0,0,20261108,20261108\r\n",
            trips.ToString(),
            times.ToString()), clock.UtcNow, TimeSpan.FromHours(1));
        var source = scratch.Source(StubFactory.NoNetwork(), clock);

        var earliest = await source.GetStopDeparturesAsync(1001, At(8, 0), At(8, 23, 59));
        var latest = await source.GetStopDeparturesAsync(1001, At(8, 0), At(8, 23, 59), default, latestFirst: true);

        Assert.Equal(100, earliest.Count);
        Assert.Equal("T0", earliest[0].JourneyRef);
        Assert.Equal("T99", earliest[^1].JourneyRef);
        Assert.Equal(100, latest.Count);
        Assert.Equal("T149", latest[0].JourneyRef);
        Assert.Equal("T50", latest[^1].JourneyRef);
        Assert.Equal("Test Agency", earliest[0].Agency);
        Assert.Equal("7", earliest[0].Line);
    }

    [Fact]
    public async Task ColumnOrder_DoesNotMatter_AndMalformedRowsAreSkipped()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        var feed = GtfsScratch.Zip(new Dictionary<string, string>
        {
            ["agency.txt"] = "agency_name,agency_id\r\n\"Quoted, Agency\",1\r\n",
            ["routes.txt"] = "route_long_name,route_short_name,agency_id,route_id\r\n\"North, to South\",7,1,10\r\n",
            ["stops.txt"] = "stop_name,stop_id,stop_code\r\nFirst,100,1001\r\nBad,101,notanumber\r\n",
            ["trips.txt"] = "trip_id,service_id,route_id\r\nT1,S,10\r\nT2,S,99\r\n",
            ["stop_times.txt"] =
                "stop_sequence,stop_id,departure_time,arrival_time,trip_id\r\n" +
                "1,100,08:00:10,08:00:00,T1\r\n" +
                "1,100,not-a-time,not-a-time,T1\r\n" +
                "x,100,09:00:00,09:00:00,T1\r\n" +
                "short\r\n" +
                "2,555,09:00:00,09:00:00,T1\r\n" +
                "1,100,08:30:00,08:30:00,T2\r\n",
            ["calendar.txt"] =
                "end_date,start_date,saturday,friday,thursday,wednesday,tuesday,monday,sunday,service_id\r\n" +
                "20261108,20261108,0,0,0,0,0,0,1,S\r\ngarbage line\r\n",
        });
        scratch.PlaceCached(feed, clock.UtcNow, TimeSpan.FromHours(1));
        var source = scratch.Source(StubFactory.NoNetwork(), clock);

        var rows = await source.GetStopDeparturesAsync(1001, At(8, 0), At(8, 23, 59));

        var row = Assert.Single(rows);   // T2 has an unknown route, the other rows are malformed or reference unknown stops
        Assert.Equal("T1", row.JourneyRef);
        Assert.Equal(At(8, 8, 0, 0), row.Time);
        Assert.Equal("Quoted, Agency", row.Agency);
        Assert.Equal("North, to South", row.RouteLongName);
    }

    [Fact]
    public async Task DstFallBackDay_EndToEnd_GivesEachTimeItsOffset()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.FromHours(3)));
        scratch.PlaceCached(GtfsScratch.MiniFeed(
            "service_id,sunday,monday,tuesday,wednesday,thursday,friday,saturday,start_date,end_date\r\nS,1,0,0,0,0,0,0,20261025,20261025\r\nP,0,0,0,0,0,0,1,20261024,20261024\r\n",
            "route_id,service_id,trip_id\r\n10,S,morning\r\n10,P,late\r\n",
            "trip_id,arrival_time,departure_time,stop_id,stop_sequence\r\nmorning,05:00:00,05:00:00,100,1\r\nlate,25:10:00,25:10:00,100,1\r\n"),
            clock.UtcNow, TimeSpan.FromHours(1));
        var source = scratch.Source(StubFactory.NoNetwork(), clock);

        var rows = await source.GetStopDeparturesAsync(
            1001,
            new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.FromHours(2)));

        Assert.Equal(2, rows.Count);
        Assert.Equal("late", rows[0].JourneyRef);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 1, 10, 0, TimeSpan.FromHours(3)), rows[0].Time);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.FromHours(2)), rows[1].Time);
    }

    [Fact]
    public async Task AtMostTwoDayIndexesStayLoaded_AndIdleOnesAreDropped()
    {
        var (source, scratch, clock) = FixtureSource();
        using var _ = scratch;

        await source.PrepareAsync(Sunday, null, default);
        await source.PrepareAsync(new DateOnly(2026, 11, 15), null, default);
        await source.PrepareAsync(new DateOnly(2026, 11, 9), null, default);

        Assert.Equal(2, source.LoadedIndexCount);

        clock.UtcNow += TimeSpan.FromMinutes(16);
        source.EvictIdle();

        Assert.Equal(0, source.LoadedIndexCount);
        Assert.Equal(FeedState.Ready, (await source.PrepareAsync(Sunday, null, default)).State);   // rebuilt on demand
    }

    private static string CultureInvariant(FormattableString text) => text.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class GtfsFeedDownloadTests
{
    [Fact]
    public async Task FreshCachedFeed_IsReusedWithoutAnyRequest()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        scratch.PlaceCached(GtfsScratch.FixtureBytes(), clock.UtcNow, TimeSpan.FromHours(23));
        var factory = StubFactory.NoNetwork();

        var result = await scratch.Source(factory, clock).PrepareAsync(new DateOnly(2026, 11, 8), null, default);

        Assert.Equal(FeedState.Ready, result.State);
        Assert.Equal(0, factory.Handler.Calls);
    }

    [Fact]
    public async Task OneDownload_IsSharedByConcurrentCallers()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        var factory = new StubFactory(StubHandler.Ok(GtfsScratch.FixtureBytes(), TimeSpan.FromMilliseconds(300)));
        var source = scratch.Source(factory, clock);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => source.PrepareAsync(new DateOnly(2026, 11, 8), null, default)));

        Assert.All(results, result => Assert.Equal(FeedState.Ready, result.State));
        Assert.Equal(1, factory.Handler.Calls);
        Assert.Equal(GtfsFeedStore.ClientName, factory.RequestedClientName);
        Assert.Equal("https", factory.Handler.LastUri!.Scheme);
        Assert.Equal("gtfs.mot.gov.il", factory.Handler.LastUri.Host);
        Assert.Single(Directory.GetFiles(scratch.Directory, "gtfs-*.zip"));
        Assert.Empty(Directory.GetFiles(scratch.Directory, "*.tmp"));
    }

    [Fact]
    public async Task StaleCache_IsRefreshedAndFilesOlderThanThreeDaysAreDeleted()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        var stale = scratch.PlaceCached(GtfsScratch.FixtureBytes(), clock.UtcNow, TimeSpan.FromHours(30), "gtfs-2026-11-06.zip");
        var ancient = scratch.PlaceCached([1, 2, 3], clock.UtcNow, TimeSpan.FromDays(4), "gtfs-2026-11-03.zip");
        var factory = new StubFactory(StubHandler.Ok(GtfsScratch.FixtureBytes()));

        var result = await scratch.Source(factory, clock).PrepareAsync(new DateOnly(2026, 11, 8), null, default);

        Assert.Equal(FeedState.Ready, result.State);
        Assert.Equal(1, factory.Handler.Calls);
        Assert.True(File.Exists(Path.Combine(scratch.Directory, "gtfs-2026-11-07.zip")));
        Assert.True(File.Exists(stale));      // 30 hours old: kept as a fallback
        Assert.False(File.Exists(ancient));   // 4 days old: deleted
    }

    [Fact]
    public async Task FailedRefresh_KeepsAndUsesTheOldFile()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        var old = scratch.PlaceCached(GtfsScratch.FixtureBytes(), clock.UtcNow, TimeSpan.FromHours(30), "gtfs-2026-11-06.zip");
        var factory = new StubFactory(StubHandler.Status(HttpStatusCode.InternalServerError));

        var result = await scratch.Source(factory, clock).PrepareAsync(new DateOnly(2026, 11, 8), null, default);

        Assert.Equal(FeedState.Ready, result.State);
        Assert.Equal(1, factory.Handler.Calls);
        Assert.True(File.Exists(old));
        Assert.Equal(GtfsScratch.FixtureBytes(), File.ReadAllBytes(old));
        Assert.Empty(Directory.GetFiles(scratch.Directory, "*.tmp"));
    }

    [Fact]
    public async Task FailedDownload_WithoutAnyCache_ReportsFailed()
    {
        using var scratch = new GtfsScratch();
        var factory = new StubFactory(StubHandler.Status(HttpStatusCode.Found));

        var result = await scratch.Source(factory, new FixedClock(GtfsScratch.Noon)).PrepareAsync(new DateOnly(2026, 11, 8), null, default);

        Assert.Equal(FeedState.Failed, result.State);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        Assert.Empty(Directory.GetFiles(scratch.Directory));
    }

    [Fact]
    public async Task DownloadClaimingMoreThanTheCap_IsRefusedBeforeReadingIt()
    {
        using var scratch = new GtfsScratch();
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ClaimedLengthContent(GtfsFeedStore.MaxDownloadBytes + 1) }));

        var result = await scratch.Source(new StubFactory(handler), new FixedClock(GtfsScratch.Noon)).PrepareAsync(new DateOnly(2026, 11, 8), null, default);

        Assert.Equal(FeedState.Failed, result.State);
        Assert.Empty(Directory.GetFiles(scratch.Directory));
    }

    [Fact]
    public async Task CorruptDownload_IsRejectedAndNeverBecomesTheCache()
    {
        using var scratch = new GtfsScratch();
        var factory = new StubFactory(StubHandler.Ok(Encoding.UTF8.GetBytes("this is not a zip file")));

        var result = await scratch.Source(factory, new FixedClock(GtfsScratch.Noon)).PrepareAsync(new DateOnly(2026, 11, 8), null, default);

        Assert.Equal(FeedState.Failed, result.State);
        Assert.Empty(Directory.GetFiles(scratch.Directory));
    }

    [Fact]
    public async Task ZipWithMissingEntries_IsRejected()
    {
        using var scratch = new GtfsScratch();
        var incomplete = GtfsScratch.Zip(new Dictionary<string, string>
        {
            ["agency.txt"] = "agency_id,agency_name\r\n1,A\r\n",
            ["stops.txt"] = "stop_id,stop_code,stop_name\r\n",
            ["trips.txt"] = "route_id,service_id,trip_id\r\n",
            ["routes.txt"] = "route_id,route_short_name\r\n",
            ["calendar.txt"] = "service_id\r\n",
        });   // no stop_times.txt
        var factory = new StubFactory(StubHandler.Ok(incomplete));

        var result = await scratch.Source(factory, new FixedClock(GtfsScratch.Noon)).PrepareAsync(new DateOnly(2026, 11, 8), null, default);

        Assert.Equal(FeedState.Failed, result.State);
        Assert.False(GtfsFeedStore.IsValidZip(Path.Combine(scratch.Directory, "gtfs-2026-11-07.zip")));
    }

    [Fact]
    public async Task ZipWithoutAnyCalendarFile_IsRejected()
    {
        using var scratch = new GtfsScratch();
        var bytes = GtfsScratch.Zip(new Dictionary<string, string>
        {
            ["agency.txt"] = "x\r\n",
            ["routes.txt"] = "x\r\n",
            ["trips.txt"] = "x\r\n",
            ["stops.txt"] = "x\r\n",
            ["stop_times.txt"] = "x\r\n",
        });
        var path = Path.Combine(scratch.Directory, "gtfs-2026-11-07.zip");
        await File.WriteAllBytesAsync(path, bytes);

        Assert.False(GtfsFeedStore.IsValidZip(path));
    }

    [Fact]
    public async Task CorruptCachedFile_IsIgnoredAndReplacedByADownload()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        scratch.PlaceCached(Encoding.UTF8.GetBytes("garbage"), clock.UtcNow, TimeSpan.FromMinutes(5));
        var factory = new StubFactory(StubHandler.Ok(GtfsScratch.FixtureBytes()));

        var result = await scratch.Source(factory, clock).PrepareAsync(new DateOnly(2026, 11, 8), null, default);

        Assert.Equal(FeedState.Ready, result.State);
        Assert.Equal(1, factory.Handler.Calls);
    }

    [Fact]
    public async Task Progress_SaysWhichDateIsBeingPrepared()
    {
        using var scratch = new GtfsScratch();
        var clock = new FixedClock(GtfsScratch.Noon);
        scratch.PlaceCached(GtfsScratch.FixtureBytes(), clock.UtcNow, TimeSpan.FromHours(1));
        var messages = new List<string>();

        await scratch.Source(StubFactory.NoNetwork(), clock).PrepareAsync(new DateOnly(2026, 11, 8), new SyncProgress(messages), default);

        Assert.Contains("Preparing the timetable for 2026-11-08", messages);
    }

    private sealed class SyncProgress(List<string> messages) : IProgress<string>
    {
        public void Report(string value) => messages.Add(value);
    }
}

/// <summary>
/// Downloads the real ~150 MB Ministry of Transport feed (measured 155,279,794 bytes on 2026-11-07, about 7 s on a fast
/// line and up to 2 minutes on a slow one) into a temp directory. Manual probe, excluded from CI.
/// </summary>
[Trait("Category", "Live")]
public sealed class GtfsFeedLiveProbeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task RealFeed_150MB_ListsPlausibleDeparturesForStop3490Tomorrow()
    {
        using var scratch = new GtfsScratch();
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        var clock = new BusPulse.Services.SystemClock();
        using var source = new GtfsFeedScheduleSource(new SingleClientFactory(client), clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<GtfsFeedScheduleSource>.Instance, scratch.Directory);
        var tomorrow = GtfsTime.LocalDate(clock.UtcNow).AddDays(1);

        var availability = await source.PrepareAsync(tomorrow, new Progress<string>(message => output.WriteLine(message)), default);
        output.WriteLine($"{availability}");
        var from = GtfsTime.At(tomorrow, 6 * 3600);
        var rows = await source.GetStopDeparturesAsync(3490, from, from.AddHours(12));

        Assert.Equal(FeedState.Ready, availability.State);
        Assert.InRange(rows.Count, 20, 100);   // a busy Bnei Brak stop: dozens of rides in 12 hours, capped at 100
        Assert.All(rows, row => Assert.Equal(3490, row.StopId));
        Assert.Equal(rows.OrderBy(row => row.Time), rows);
        output.WriteLine($"{rows.Count} departures, first {rows[0].Time:O} line {rows[0].Line}");
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
