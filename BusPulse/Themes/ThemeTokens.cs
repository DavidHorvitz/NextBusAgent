using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace BusPulse.Themes;

/// <summary>
/// Turns the "Bp.Light.*" / "Bp.Dark.*" colours of Theme.xaml into the "Bp.*" alias brushes that views use.
/// See docs/DESIGN.md for the token table.
/// </summary>
public static class ThemeTokens
{
    public const string Prefix = "Bp.";
    public const string LightPrefix = "Bp.Light.";
    public const string DarkPrefix = "Bp.Dark.";

    /// <summary>The merged dictionary that holds the tokens (the one that defines <c>Bp.Light.Neutral.1</c>).</summary>
    public static ResourceDictionary? FindTokenDictionary(ResourceDictionary root)
    {
        // Not root.Contains: that also searches merged dictionaries, so it would return the App dictionary itself.
        if (root.Keys.OfType<string>().Contains(LightPrefix + "Neutral.1", StringComparer.Ordinal))
        {
            return root;
        }

        foreach (var merged in root.MergedDictionaries)
        {
            if (FindTokenDictionary(merged) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Every token name (without the mode prefix) and its colour in the requested mode. A token is either a Color or, for a
    /// role alias, a string naming a scale step of the same mode ("Neutral.12"). XAML &lt;StaticResource x:Key&gt; entries
    /// were tried for aliases and lost keys when the dictionary loaded, hence the strings.
    /// </summary>
    public static IReadOnlyDictionary<string, Color> Read(ResourceDictionary tokens, bool dark)
    {
        var prefix = dark ? DarkPrefix : LightPrefix;
        var result = new SortedDictionary<string, Color>(StringComparer.Ordinal);
        foreach (var key in tokens.Keys.OfType<string>().Where(key => key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var value = tokens[key];
            if (value is string alias)
            {
                value = tokens[prefix + alias] ?? throw new InvalidOperationException($"{key} names {alias}, which is not a {prefix} token.");
            }

            if (value is not Color color)
            {
                throw new InvalidOperationException($"{key} is a {value?.GetType().Name ?? "null"}, not a Color or a token name.");
            }

            result[key[prefix.Length..]] = color;
        }

        return result;
    }

    /// <summary>Writes one frozen <see cref="SolidColorBrush"/> per token into <paramref name="target"/> as "Bp.&lt;Name&gt;".</summary>
    public static void Apply(ResourceDictionary target, ResourceDictionary tokens, bool dark)
    {
        foreach (var (name, color) in Read(tokens, dark))
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            target[Prefix + name] = brush;
        }
    }

    /// <summary>WCAG 2.x relative luminance.</summary>
    public static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    /// <summary>WCAG 2.x contrast ratio, 1 to 21.</summary>
    public static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Parses a GTFS colour ("RRGGBB", optional leading '#'); null when empty or malformed.</summary>
    public static Color? ParseHex(string? value)
    {
        var text = value?.Trim().TrimStart('#');
        if (text is not { Length: 6 } || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return null;
        }

        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }
}

/// <summary>The colours of a line badge, from GTFS <c>route_color</c> and <c>route_text_color</c>.</summary>
public static class LineBadgeColors
{
    /// <summary>Minimum contrast for the line number (14 px bold is not "large text" in WCAG terms).</summary>
    public const double MinimumContrast = 4.5;

    /// <summary>
    /// Null when the feed gives no usable route colour (the badge is then neutral and shows the operator). Otherwise the
    /// fill and a text colour: the feed's text colour (GTFS default black) when it reaches <see cref="MinimumContrast"/> on
    /// the fill, else black or white, whichever contrasts more.
    /// </summary>
    public static (Color Fill, Color Text)? Resolve(string? routeColor, string? routeTextColor)
    {
        if (ThemeTokens.ParseHex(routeColor) is not { } fill)
        {
            return null;
        }

        var text = ThemeTokens.ParseHex(routeTextColor) ?? Colors.Black;
        if (ThemeTokens.Contrast(fill, text) >= MinimumContrast)
        {
            return (fill, text);
        }

        return (fill, ThemeTokens.Contrast(fill, Colors.Black) >= ThemeTokens.Contrast(fill, Colors.White) ? Colors.Black : Colors.White);
    }
}
