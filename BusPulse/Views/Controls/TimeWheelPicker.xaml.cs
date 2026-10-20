using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;

namespace BusPulse.Views.Controls;

/// <summary>A wheel-style time-of-day picker: mouse wheel, drag, and Up/Down/PageUp/PageDown from the keyboard.</summary>
public partial class TimeWheelPicker : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(TimeSpan),
        typeof(TimeWheelPicker),
        new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TimeWheelPicker)d).Render()));

    private const double PixelsPerStep = 18;
    private Point _dragStart;
    private double _dragRemainder;
    private bool _dragging;

    public TimeWheelPicker()
    {
        InitializeComponent();
        Render();
    }

    public TimeSpan Value
    {
        get => (TimeSpan)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private int Hour => Math.Clamp(Value.Hours, 0, 23);

    private int Minute => Math.Clamp(Value.Minutes, 0, 59);

    private void Render()
    {
        if (HourNow is null)
        {
            return;
        }

        HourPrev.Text = Format((Hour + 23) % 24);
        HourNow.Text = Format(Hour);
        HourNext.Text = Format((Hour + 1) % 24);
        MinutePrev.Text = Format((Minute + 59) % 60);
        MinuteNow.Text = Format(Minute);
        MinuteNext.Text = Format((Minute + 1) % 60);
        AutomationProperties.SetHelpText(this, $"{Format(Hour)}:{Format(Minute)}");
    }

    private void Step(bool hours, int delta)
    {
        if (hours)
        {
            Value = new TimeSpan(((Hour + delta) % 24 + 24) % 24, Minute, 0);
        }
        else
        {
            Value = new TimeSpan(Hour, ((Minute + delta) % 60 + 60) % 60, 0);
        }
    }

    private bool ColumnIsHours(object sender) => (sender as FrameworkElement)?.Tag as string == "hour";

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var over = Mouse.DirectlyOver as DependencyObject;
        var hours = IsInside(over, HourColumn);
        Step(hours, e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var hours = !MinuteColumn.IsKeyboardFocusWithin && !IsFocusOnMinutes;
        switch (e.Key)
        {
            case Key.Up:
                Step(hours, 1);
                break;
            case Key.Down:
                Step(hours, -1);
                break;
            case Key.PageUp:
                Step(true, 1);
                break;
            case Key.PageDown:
                Step(true, -1);
                break;
            case Key.Left or Key.Right:
                IsFocusOnMinutes = !IsFocusOnMinutes;
                Render();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private bool IsFocusOnMinutes { get; set; }

    private void OnFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        Frame.BorderBrush = (Brush)FindResource(IsKeyboardFocusWithin ? "AccentBrush" : "CardStrokeColorDefaultBrush");

    private void OnColumnDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _dragging = true;
        _dragStart = e.GetPosition(this);
        _dragRemainder = 0;
        ((UIElement)sender).CaptureMouse();
    }

    private void OnColumnMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var position = e.GetPosition(this);
        _dragRemainder += _dragStart.Y - position.Y;
        _dragStart = position;
        var steps = (int)(_dragRemainder / PixelsPerStep);
        if (steps != 0)
        {
            _dragRemainder -= steps * PixelsPerStep;
            Step(ColumnIsHours(sender), steps);
        }
    }

    private void OnColumnUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, container))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element);
        }

        return false;
    }

    private static string Format(int value) => value.ToString("00", CultureInfo.InvariantCulture);
}
