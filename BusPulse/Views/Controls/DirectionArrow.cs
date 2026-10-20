using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Control = System.Windows.Controls.Control;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace BusPulse.Views.Controls;

/// <summary>An arrow that rotates smoothly to a compass bearing (0 = north, 90 = east); it never mirrors in RTL layouts.</summary>
public sealed class DirectionArrow : Control
{
    public static readonly DependencyProperty BearingProperty = DependencyProperty.Register(
        nameof(Bearing), typeof(double), typeof(DirectionArrow), new PropertyMetadata(90d, OnBearingChanged));

    private readonly RotateTransform _rotate = new(0);
    private readonly TextBlock _glyph;

    public DirectionArrow()
    {
        // U+27A4 points right (east), so the rotation is bearing - 90.
        _glyph = new TextBlock
        {
            Text = "➤",
            FontSize = 16,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _rotate,
            FlowDirection = System.Windows.FlowDirection.LeftToRight
        };
        _glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        AddVisualChild(_glyph);
        Width = 24;
        Height = 24;
        _rotate.Angle = 0;
        Loaded += (_, _) => _rotate.Angle = Bearing - 90;
    }

    public double Bearing
    {
        get => (double)GetValue(BearingProperty);
        set => SetValue(BearingProperty, value);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _glyph;

    protected override Size MeasureOverride(Size constraint)
    {
        _glyph.Measure(constraint);
        return new Size(Width, Height);
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        _glyph.Arrange(new Rect(arrangeBounds));
        return arrangeBounds;
    }

    private static void OnBearingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var arrow = (DirectionArrow)sender;
        var target = (double)e.NewValue - 90;
        if (!SystemParameters.ClientAreaAnimation || !arrow.IsLoaded)
        {
            arrow._rotate.Angle = target;
            return;
        }

        // Rotate along the shortest path.
        var current = arrow._rotate.Angle;
        var delta = ((target - current) % 360 + 540) % 360 - 180;
        arrow._rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(current + delta, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }
}
