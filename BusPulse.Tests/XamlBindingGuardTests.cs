using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace BusPulse.Tests;

/// <summary>
/// XAML binding mistakes only show at runtime (the app then shows no window at all), so the ones seen so far are guarded here.
/// </summary>
public sealed class XamlBindingGuardTests
{
    [Fact]
    public void DatePickerDisplayDateLimits_AreBoundOneWay()
    {
        // DatePicker.DisplayDateStart/End bind two-way by default; a get-only view-model property then throws
        // InvalidOperationException while the window loads (seen 2026-11-07 with MaxDate).
        var xaml = File.ReadAllText(FindView("TripPlannerView.xaml"));

        var bindings = Regex.Matches(xaml, @"DisplayDate(Start|End)=""\{Binding[^}]*\}""");

        Assert.NotEmpty(bindings);
        Assert.All(bindings, binding => Assert.Contains("Mode=OneWay", binding.Value));
    }

    private static string FindView(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "BusPulse", "Views", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not find {name} above {AppContext.BaseDirectory}.");
    }
}
