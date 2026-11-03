using BusPulse.Configuration;
using BusPulse.Infrastructure;
using BusPulse.Services.Location;
using BusPulse.Services.Stops;
using BusPulse.Services.Trips;
using BusPulse.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>The destination search finds any stop by name, not only saved places (seen live: "אסף הרופא" found nothing).</summary>
public sealed class DestinationSearchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T10:00:00+00:00");
    private static readonly Stop Origin = new("100", "Origin stop", "Bnei Brak", 32.10, 34.80, Id: 1);
    private static readonly Stop Hospital1 = new("200", "אסף הרופא/כניסה ראשית", "באר יעקב", 31.94, 34.84, Id: 2);
    private static readonly Stop Hospital2 = new("201", "אסף הרופא/כניסה ראשית", "באר יעקב", 31.94, 34.84, Id: 3);
    private static readonly Stop Junction = new("202", "צומת אסף הרופא", "באר יעקב", 31.95, 34.85, Id: 4);
    private static readonly Stop Elsewhere = new("300", "Dizengoff Center", "Tel Aviv", 32.07, 34.77, Id: 5);

    [Fact]
    public async Task TypingPartOfAStopName_SuggestsMatchingStops_NamesStartingWithItFirst()
    {
        var f = new Fixture();

        f.Vm.DestinationQuery = "אסף הרופא";
        await f.Vm.UpdateSuggestionsAsync();

        Assert.Equal(["אסף הרופא/כניסה ראשית, באר יעקב", "צומת אסף הרופא, באר יעקב"], f.Vm.Suggestions.Select(s => s.DisplayName).ToArray());
        Assert.All(f.Vm.Suggestions, suggestion => Assert.NotNull(suggestion.Stop));
    }

    [Fact]
    public async Task SavedPlacesComeBeforeStops()
    {
        var f = new Fixture();
        f.Places.Items.Add(new SavedPlace { Name = "אסף הרופא (אמא)", StopCode = "200" });

        f.Vm.DestinationQuery = "אסף";
        await f.Vm.UpdateSuggestionsAsync();

        Assert.NotNull(f.Vm.Suggestions[0].Place);
        Assert.Equal(3, f.Vm.Suggestions.Count);
    }

    [Fact]
    public async Task OneCharacter_DoesNotSearchTheWholeStopList()
    {
        var f = new Fixture();

        f.Vm.DestinationQuery = "א";
        await f.Vm.UpdateSuggestionsAsync();

        Assert.Empty(f.Vm.Suggestions);
    }

    [Fact]
    public async Task ChoosingAStop_MakesItTheDestinationAndAddsItToTheList()
    {
        var f = new Fixture();
        f.Vm.DestinationQuery = "צומת";
        await f.Vm.UpdateSuggestionsAsync();

        f.Vm.ChooseSuggestionCommand.Execute(f.Vm.Suggestions[0]);

        Assert.Same(Junction, f.Vm.SelectedDestination!.Stop);
        Assert.Contains(f.Vm.SelectedDestination, f.Vm.Choices);
    }

    [Fact]
    public async Task FindTrips_WithTypedTextButNoPick_UsesTheBestMatch()
    {
        var f = new Fixture();
        f.Vm.DestinationQuery = "אסף הרופא";

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal(1, f.Planner.Calls);
        Assert.Equal(Hospital1.Latitude, f.Planner.LastRequest!.DestinationLatitude);
        Assert.NotEqual("SetDestinationFirst", f.Vm.StatusText);
    }

    [Fact]
    public async Task FindTrips_WithTextThatMatchesNothing_StillAsksForADestination()
    {
        var f = new Fixture();
        f.Vm.DestinationQuery = "zzzz";

        await f.Vm.PlanCommand.ExecuteAsync(null);

        Assert.Equal(0, f.Planner.Calls);
        Assert.Equal("SetDestinationFirst", f.Vm.StatusText);
    }

    private sealed class Fixture
    {
        public Fixture()
        {
            var settings = new MemorySettings();
            Places = new SavedPlaceCollection(settings);
            Planner = new FakeTripPlanner();
            Vm = new TripPlannerViewModel(
                settings, Places, new TemplateLocalizer(), Planner, new FakeCatalog(Origin, Hospital1, Hospital2, Junction, Elsewhere),
                new FakeLocation { Result = new LocationResult(LocationStatus.Ok, 32.10, 34.80, 20) }, new FakeHistory(), new FakeReminder(),
                new RecordingNotifier(), new FixedClock(Now), new ImmediateDispatcher(), NullLogger<TripPlannerViewModel>.Instance, new FakeCalendar())
            {
                DebounceDelay = TimeSpan.Zero
            };
        }

        public SavedPlaceCollection Places { get; }
        public FakeTripPlanner Planner { get; }
        public TripPlannerViewModel Vm { get; }
    }
}
