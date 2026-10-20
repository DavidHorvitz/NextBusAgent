using System.Text;
using BusPulse.Services.Calendar;
using BusPulse.ViewModels;
using Xunit;

namespace BusPulse.Tests;

public sealed class CalendarTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-20T10:00:00+00:00");

    [Fact]
    public void Pkce_ChallengeMatchesTheRfc7636Example()
    {
        // The worked example from RFC 7636, Appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", GoogleOAuth.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void AuthUrl_IsReadOnlyCalendarScope_WithPkceAndState()
    {
        var url = GoogleOAuth.BuildAuthUrl("client-id", "http://127.0.0.1:5000/callback", "challenge", "state-1");
        var text = url.ToString();

        Assert.StartsWith(GoogleOAuth.AuthEndpoint, text);
        Assert.Contains("calendar.events.readonly", text);
        Assert.DoesNotContain("auth/calendar&", text);
        Assert.Contains("code_challenge_method=S256", text);
        Assert.Contains("state=state-1", text);
        Assert.Contains("redirect_uri=http%3A%2F%2F127.0.0.1%3A5000%2Fcallback", text);
    }

    [Fact]
    public void TokenResponse_ErrorOrMissingToken_IsNotSuccess()
    {
        Assert.False(GoogleOAuth.ParseTokenResponse("""{"error":"invalid_grant"}"""u8).IsSuccess);
        Assert.Equal("invalid_grant", GoogleOAuth.ParseTokenResponse("""{"error":"invalid_grant"}"""u8).Error);
        Assert.False(GoogleOAuth.ParseTokenResponse("{}"u8).IsSuccess);
        Assert.False(GoogleOAuth.ParseTokenResponse("[]"u8).IsSuccess);
    }

    [Fact]
    public void TokenResponse_ClampsAbsurdLifetimes()
    {
        var result = GoogleOAuth.ParseTokenResponse("""{"access_token":"a","expires_in":99999999}"""u8);

        Assert.True(result.IsSuccess);
        Assert.Equal(86_400, result.ExpiresInSeconds);
    }

    [Fact]
    public void Events_SkipAllDayAndMalformedItems_AndKeepTheRest()
    {
        const string json = """
            {"items":[
              {"id":"1","summary":"Dentist","location":"Dizengoff 10, Tel Aviv","start":{"dateTime":"2026-10-20T12:00:00+03:00"}},
              {"id":"2","summary":"Holiday","start":{"date":"2026-10-21"}},
              {"summary":"no id","start":{"dateTime":"2026-10-20T13:00:00+03:00"}},
              {"id":"3","start":{"dateTime":"not a date"}}
            ]}
            """;

        var events = GoogleOAuth.ParseEvents(Encoding.UTF8.GetBytes(json));

        Assert.Equal(["1"], events.Select(e => e.Id).ToArray());
        Assert.Equal("Dizengoff 10, Tel Aviv", events[0].Location);
    }

    [Fact]
    public void Suggester_MatchesEventLocationsToSavedPlaces_InTheNext48Hours()
    {
        var places = new[] { new BusPulse.Configuration.SavedPlace { Name = "Work", StopCode = "1" }, new BusPulse.Configuration.SavedPlace { Name = "Tel Aviv Office", StopCode = "2" } };
        var events = new[]
        {
            new CalendarEvent("a", "Meeting", Now.AddHours(3), "tel aviv office, floor 4"),
            new CalendarEvent("b", "Too soon", Now.AddMinutes(5), "Work"),
            new CalendarEvent("c", "Too far", Now.AddHours(60), "Work"),
            new CalendarEvent("d", "No place", Now.AddHours(3), "Jerusalem"),
            new CalendarEvent("e", "No location", Now.AddHours(3), null)
        };

        var suggestions = CalendarTripSuggester.Suggest(events, places, Now);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("a", suggestion.Event.Id);
        Assert.Equal("Tel Aviv Office", suggestion.Place.Name);
    }

    [Fact]
    public void Settings_WhenNotConfigured_ExplainsInsteadOfConnecting()
    {
        var f = new ShellAndSettingsTests.Fixture();
        f.Calendar.IsConfigured = false;

        f.SettingsTab.ConnectCalendarCommand.Execute(null);

        Assert.Equal("CalendarNotConfigured", f.SettingsTab.CalendarStatus);
    }

    [Fact]
    public async Task Settings_ConnectSuccess_ShowsConnectedAndDisconnectClearsIt()
    {
        var f = new ShellAndSettingsTests.Fixture();

        await f.SettingsTab.ConnectCalendarCommand.ExecuteAsync(null);

        Assert.True(f.SettingsTab.IsCalendarConnected);
        Assert.Equal("CalendarConnected", f.SettingsTab.CalendarStatus);
        Assert.False(f.SettingsTab.IsConnectingCalendar);

        f.SettingsTab.DisconnectCalendarCommand.Execute(null);

        Assert.False(f.SettingsTab.IsCalendarConnected);
        Assert.Equal(1, f.Calendar.Disconnects);
        Assert.Equal("CalendarDisconnected", f.SettingsTab.CalendarStatus);
    }

    [Theory]
    [InlineData(CalendarConnectStatus.Cancelled, "CalendarCancelled")]
    [InlineData(CalendarConnectStatus.StateMismatch, "CalendarFailed")]
    [InlineData(CalendarConnectStatus.Failed, "CalendarFailed")]
    public async Task Settings_FailedConnect_ReportsTheReasonAndStaysDisconnected(CalendarConnectStatus status, string message)
    {
        var f = new ShellAndSettingsTests.Fixture();
        f.Calendar.Result = new CalendarConnectResult(status);

        await f.SettingsTab.ConnectCalendarCommand.ExecuteAsync(null);

        Assert.False(f.SettingsTab.IsCalendarConnected);
        Assert.Equal(message, f.SettingsTab.CalendarStatus);
    }
}
