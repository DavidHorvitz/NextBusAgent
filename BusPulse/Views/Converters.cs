using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;

namespace BusPulse.Views;

/// <summary>Visible when the bound collection count (or integer) is greater than zero.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Binds a radio button to an integer selection: checked when the value equals the parameter.</summary>
public sealed class IndexToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target) && index == target;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var target)
            ? target
            : Binding.DoNothing;
}

/// <summary>Line pills are coloured by operator; every colour keeps white text above 4.5:1 contrast.</summary>
public sealed class OperatorBrushConverter : IValueConverter
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(0x14, 0x7D, 0x70), // teal
        Color.FromRgb(0x1D, 0x5F, 0xB4), // blue
        Color.FromRgb(0x8A, 0x3F, 0xA8), // purple
        Color.FromRgb(0xB0, 0x4A, 0x14), // brown
        Color.FromRgb(0x2E, 0x6B, 0x2E), // green
        Color.FromRgb(0xA3, 0x2A, 0x4C), // crimson
        Color.FromRgb(0x4A, 0x57, 0x8C), // indigo
        Color.FromRgb(0x5C, 0x5C, 0x5C)  // grey
    ];

    private static readonly Dictionary<string, int> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["דן"] = 1, ["Dan"] = 1,
        ["אגד"] = 4, ["Egged"] = 4,
        ["מטרופולין"] = 2, ["Metropoline"] = 2,
        ["קווים"] = 3, ["Kavim"] = 3,
        ["סופרבוס"] = 6, ["Superbus"] = 6
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var name = value as string;
        if (string.IsNullOrWhiteSpace(name))
        {
            return new SolidColorBrush(Palette[0]);
        }

        // Stable hash (string.GetHashCode is randomised per process).
        var index = Known.TryGetValue(name.Trim(), out var known)
            ? known
            : name.Aggregate(17, (hash, character) => hash * 31 + character) is var mixed ? Math.Abs(mixed % Palette.Length) : 0;
        var brush = new SolidColorBrush(Palette[index % Palette.Length]);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Visible when the bound value is false (for "connect" while not connected).</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool flag && !flag ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
