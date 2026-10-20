using System.IO;
using BusPulse.Configuration;
using BusPulse.Services;
using BusPulse.Services.LiveData;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

public sealed class HistoryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "buspulse-history-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new(DateTimeOffset.Parse("2026-10-20T10:00:00+00:00"));

    private string FilePath => Path.Combine(_directory, "user_history.json");

    [Fact]
    public void Record_OrdersMostRecentFirst_AndDeduplicatesByPlace()
    {
        var history = Create();
        history.Record("a", "Home");
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        history.Record("b", "Work");
        _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
        history.Record("a", "Home");

        Assert.Equal(["Home", "Work"], history.Recent.Select(entry => entry.Name).ToArray());
    }

    [Fact]
    public void Record_IsCappedAtTheMaximum()
    {
        var history = Create();
        for (var i = 0; i < HistoryService.MaxEntries + 10; i++)
        {
            _clock.UtcNow = _clock.UtcNow.AddSeconds(1);
            history.Record("p" + i, "Place " + i);
        }

        Assert.Equal(HistoryService.MaxEntries, history.Recent.Count);
        Assert.Equal("Place " + (HistoryService.MaxEntries + 9), history.Recent[0].Name);
    }

    [Fact]
    public void History_PersistsAcrossInstances_AndClearDeletesTheFile()
    {
        Create().Record("a", "Home");

        var reloaded = Create();
        Assert.Single(reloaded.Recent);
        Assert.True(File.Exists(FilePath));

        reloaded.Clear();

        Assert.Empty(reloaded.Recent);
        Assert.False(File.Exists(FilePath));
        Assert.Empty(Create().Recent);
    }

    [Fact]
    public void CorruptOrOversizedFiles_AreIgnored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "{ not json");
        Assert.Empty(Create().Recent);

        File.WriteAllText(FilePath, new string('x', 70 * 1024));
        Assert.Empty(Create().Recent);
    }

    [Theory]
    [InlineData("", "Home")]
    [InlineData("a", "")]
    public void Record_IgnoresEmptyValues(string id, string name)
    {
        var history = Create();

        history.Record(id, name);

        Assert.Empty(history.Recent);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private HistoryService Create() => new(_clock, NullLogger<HistoryService>.Instance, FilePath);
}
