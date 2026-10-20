using BusPulse.Configuration;

namespace BusPulse.Services.Calendar;

/// <summary>An upcoming event whose location matches a saved place, so a trip can be planned for it.</summary>
public sealed record CalendarSuggestion(CalendarEvent Event, SavedPlace Place);

/// <summary>
/// Matches events to saved places by name, entirely on this machine: addresses are never sent to a geocoding service.
/// An event matches when its location text contains the place name (or the other way round), ignoring case.
/// </summary>
public static class CalendarTripSuggester
{
    private static readonly TimeSpan MinLead = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Horizon = TimeSpan.FromHours(48);

    public static IReadOnlyList<CalendarSuggestion> Suggest(IEnumerable<CalendarEvent> events, IEnumerable<SavedPlace> places, DateTimeOffset now)
    {
        var placeList = places.Where(place => !string.IsNullOrWhiteSpace(place.Name)).ToList();
        var suggestions = new List<CalendarSuggestion>();
        foreach (var calendarEvent in events.OrderBy(item => item.Start))
        {
            if (string.IsNullOrWhiteSpace(calendarEvent.Location) ||
                calendarEvent.Start < now + MinLead ||
                calendarEvent.Start > now + Horizon)
            {
                continue;
            }

            var match = placeList
                .Where(place => Matches(calendarEvent.Location, place.Name))
                .OrderByDescending(place => place.Name.Length)
                .FirstOrDefault();
            if (match is not null)
            {
                suggestions.Add(new CalendarSuggestion(calendarEvent, match));
            }
        }

        return suggestions;
    }

    private static bool Matches(string location, string placeName) =>
        location.Contains(placeName, StringComparison.CurrentCultureIgnoreCase) ||
        placeName.Contains(location.Trim(), StringComparison.CurrentCultureIgnoreCase);
}
