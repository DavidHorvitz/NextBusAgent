using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BusPulse.Themes;
using BusPulse.ViewModels;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;

namespace BusPulse.Views.Controls;

// Design-system controls. Templates live in Themes/Theme.xaml; the spec is docs/DESIGN.md.

/// <summary>Pill with the line number. Colours come from GTFS route_color / route_text_color only, never invented.</summary>
public sealed class LineBadge : Control
{
    public static readonly DependencyProperty LineNameProperty = DependencyProperty.Register(
        nameof(LineName), typeof(string), typeof(LineBadge), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty OperatorNameProperty = DependencyProperty.Register(
        nameof(OperatorName), typeof(string), typeof(LineBadge), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty RouteColorProperty = DependencyProperty.Register(
        nameof(RouteColor), typeof(string), typeof(LineBadge), new PropertyMetadata(null, OnColorChanged));

    public static readonly DependencyProperty RouteTextColorProperty = DependencyProperty.Register(
        nameof(RouteTextColor), typeof(string), typeof(LineBadge), new PropertyMetadata(null, OnColorChanged));

    private static readonly DependencyPropertyKey FillPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(Fill), typeof(Brush), typeof(LineBadge), new PropertyMetadata(Brushes.Transparent));

    private static readonly DependencyPropertyKey TextBrushPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(TextBrush), typeof(Brush), typeof(LineBadge), new PropertyMetadata(Brushes.Black));

    private static readonly DependencyPropertyKey HasRouteColorPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasRouteColor), typeof(bool), typeof(LineBadge), new PropertyMetadata(false));

    public static readonly DependencyProperty FillProperty = FillPropertyKey.DependencyProperty;
    public static readonly DependencyProperty TextBrushProperty = TextBrushPropertyKey.DependencyProperty;
    public static readonly DependencyProperty HasRouteColorProperty = HasRouteColorPropertyKey.DependencyProperty;

    static LineBadge() => DefaultStyleKeyProperty.OverrideMetadata(typeof(LineBadge), new FrameworkPropertyMetadata(typeof(LineBadge)));

    public string LineName { get => (string)GetValue(LineNameProperty); set => SetValue(LineNameProperty, value); }

    public string OperatorName { get => (string)GetValue(OperatorNameProperty); set => SetValue(OperatorNameProperty, value); }

    /// <summary>GTFS routes.txt route_color, "RRGGBB". Empty or null: neutral badge plus the operator name.</summary>
    public string? RouteColor { get => (string?)GetValue(RouteColorProperty); set => SetValue(RouteColorProperty, value); }

    /// <summary>GTFS routes.txt route_text_color; replaced by black or white when it fails 4.5:1 on the fill.</summary>
    public string? RouteTextColor { get => (string?)GetValue(RouteTextColorProperty); set => SetValue(RouteTextColorProperty, value); }

    public Brush Fill => (Brush)GetValue(FillProperty);

    public Brush TextBrush => (Brush)GetValue(TextBrushProperty);

    public bool HasRouteColor => (bool)GetValue(HasRouteColorProperty);

    private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var badge = (LineBadge)d;
        if (LineBadgeColors.Resolve(badge.RouteColor, badge.RouteTextColor) is { } colors)
        {
            badge.SetValue(FillPropertyKey, Frozen(colors.Fill));
            badge.SetValue(TextBrushPropertyKey, Frozen(colors.Text));
            badge.SetValue(HasRouteColorPropertyKey, true);
        }
        else
        {
            badge.SetValue(HasRouteColorPropertyKey, false);
        }
    }

    private static SolidColorBrush Frozen(System.Windows.Media.Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Status of a departure as an icon plus a word; never colour alone.</summary>
public sealed class StatusChip : Control
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(DepartureStatus), typeof(StatusChip), new PropertyMetadata(DepartureStatus.Scheduled));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusChip), new PropertyMetadata(string.Empty));

    static StatusChip() => DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusChip), new FrameworkPropertyMetadata(typeof(StatusChip)));

    public DepartureStatus Status { get => (DepartureStatus)GetValue(StatusProperty); set => SetValue(StatusProperty, value); }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}

public enum BannerTone
{
    Info,
    Warning,
    Danger,
    Offline
}

/// <summary>Inline message for offline, no service and location problems; one per section at most.</summary>
public sealed class Banner : Control
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(BannerTone), typeof(Banner), new PropertyMetadata(BannerTone.Info));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Banner), new PropertyMetadata(string.Empty));

    static Banner() => DefaultStyleKeyProperty.OverrideMetadata(typeof(Banner), new FrameworkPropertyMetadata(typeof(Banner)));

    public BannerTone Tone { get => (BannerTone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
}

/// <summary>Text that cross-fades when it changes (150 ms), for countdowns. Instant when motion is reduced.</summary>
[TemplatePart(Name = "PART_Front", Type = typeof(TextBlock))]
[TemplatePart(Name = "PART_Back", Type = typeof(TextBlock))]
public sealed class TickerText : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TickerText), new PropertyMetadata(string.Empty, OnTextChanged));

    public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
        nameof(TextAlignment), typeof(TextAlignment), typeof(TickerText), new PropertyMetadata(TextAlignment.Left));

    private TextBlock? _front;
    private TextBlock? _back;

    static TickerText() => DefaultStyleKeyProperty.OverrideMetadata(typeof(TickerText), new FrameworkPropertyMetadata(typeof(TickerText)));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public TextAlignment TextAlignment { get => (TextAlignment)GetValue(TextAlignmentProperty); set => SetValue(TextAlignmentProperty, value); }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _front = GetTemplateChild("PART_Front") as TextBlock;
        _back = GetTemplateChild("PART_Back") as TextBlock;
        if (_front is not null)
        {
            _front.Text = Text;
        }
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ticker = (TickerText)d;
        if (ticker._front is null || ticker._back is null)
        {
            return;
        }

        var old = e.OldValue as string ?? string.Empty;
        var current = e.NewValue as string ?? string.Empty;
        ticker._front.Text = current;
        if (!ticker.IsLoaded || Motion.IsReduced || old.Length == 0)
        {
            ticker._back.BeginAnimation(OpacityProperty, null);
            ticker._back.Opacity = 0;
            ticker._front.BeginAnimation(OpacityProperty, null);
            ticker._front.Opacity = 1;
            return;
        }

        ticker._back.Text = old;
        ticker._back.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Motion.Quick));
        ticker._front.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Quick));
    }
}

/// <summary>
/// Motion rules (DESIGN.md): 150-250 ms, fade and slide for content that appears, nothing for content that stays, and
/// no motion at all when Windows "Animation effects" is off.
/// </summary>
public static class Motion
{
    public static readonly Duration Standard = new(TimeSpan.FromMilliseconds(200));

    /// <summary>The 150 ms cross-fade of changing values (countdowns, time fields).</summary>
    public static readonly Duration Quick = new(TimeSpan.FromMilliseconds(150));

    public static readonly DependencyProperty FadeInOnShowProperty = DependencyProperty.RegisterAttached(
        "FadeInOnShow", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnFadeInOnShowChanged));

    public static readonly DependencyProperty PulseProperty = DependencyProperty.RegisterAttached(
        "Pulse", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPulseChanged));

    /// <summary>Set by tests and screenshots to make every frame final.</summary>
    public static bool ForceReduced { get; set; }

    /// <summary>
    /// True when Windows "Animation effects" (Settings, Accessibility, Visual effects) is off; WPF reads it as
    /// SPI_GETCLIENTAREAANIMATION. Read at the start of every animation, so a change applies without a restart.
    /// </summary>
    public static bool IsReduced => ForceReduced || !SystemParameters.ClientAreaAnimation;

    public static bool GetFadeInOnShow(DependencyObject element) => (bool)element.GetValue(FadeInOnShowProperty);

    public static void SetFadeInOnShow(DependencyObject element, bool value) => element.SetValue(FadeInOnShowProperty, value);

    public static bool GetPulse(DependencyObject element) => (bool)element.GetValue(PulseProperty);

    public static void SetPulse(DependencyObject element, bool value) => element.SetValue(PulseProperty, value);

    private static void OnFadeInOnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.IsVisibleChanged -= FadeIn;
        if ((bool)e.NewValue)
        {
            element.IsVisibleChanged += FadeIn;
        }
    }

    private static void FadeIn(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not UIElement element || !(bool)e.NewValue || IsReduced)
        {
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new TranslateTransform();
        element.RenderTransform = slide;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Standard) { EasingFunction = ease });
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-8, 0, Standard) { EasingFunction = ease });
    }

    private static void OnPulseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= StartPulse;
        element.Unloaded -= StopPulse;
        if ((bool)e.NewValue)
        {
            element.Loaded += StartPulse;
            element.Unloaded += StopPulse;
            if (element.IsLoaded)
            {
                StartPulse(element, new RoutedEventArgs());
            }
        }
    }

    private static void StartPulse(object sender, RoutedEventArgs e)
    {
        if (sender is not UIElement element || IsReduced)
        {
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.55, new Duration(TimeSpan.FromMilliseconds(900)))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private static void StopPulse(object sender, RoutedEventArgs e) => (sender as UIElement)?.BeginAnimation(UIElement.OpacityProperty, null);
}
