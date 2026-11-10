using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace BusPulse.Views.Controls;

/// <summary>
/// Time of day as two typed fields. Buttons step the minute by 5 and the hour by 1; arrow keys step the focused field by 1;
/// digits can be typed directly (committed on Enter, on leaving the field, or when two digits are in). A changed value
/// cross-fades over 150 ms.
/// </summary>
public partial class TimeFieldPicker : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(TimeSpan),
        typeof(TimeFieldPicker),
        new FrameworkPropertyMetadata(TimeSpan.Zero, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TimeFieldPicker)d).Render(animate: true)));

    public const int ButtonMinuteStep = 5;
    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    public TimeFieldPicker()
    {
        InitializeComponent();
        Render(animate: false);
    }

    public TimeSpan Value
    {
        get => (TimeSpan)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Moves the time by <paramref name="delta"/>, wrapping within the day (the date is handled by the page).</summary>
    public void Step(TimeSpan delta)
    {
        var next = TimeSpan.FromTicks((Value + delta).Ticks % Day.Ticks);
        Value = next < TimeSpan.Zero ? next + Day : next;
    }

    private void Render(bool animate)
    {
        var hour = Value.Hours.ToString("00", CultureInfo.InvariantCulture);
        var minute = Value.Minutes.ToString("00", CultureInfo.InvariantCulture);
        Show(HourBox, hour, animate);
        Show(MinuteBox, minute, animate);
    }

    private static void Show(TextBox box, string text, bool animate)
    {
        if (box.Text == text)
        {
            return;
        }

        box.Text = text;
        if (animate && box.IsLoaded && !Motion.IsReduced)
        {
            box.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, Motion.Quick));
        }
    }

    private void OnHourUp(object sender, RoutedEventArgs e) => Step(TimeSpan.FromHours(1));

    private void OnHourDown(object sender, RoutedEventArgs e) => Step(TimeSpan.FromHours(-1));

    private void OnMinuteUp(object sender, RoutedEventArgs e) => Step(TimeSpan.FromMinutes(ButtonMinuteStep));

    private void OnMinuteDown(object sender, RoutedEventArgs e) => Step(TimeSpan.FromMinutes(-ButtonMinuteStep));

    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(System.Windows.DataFormats.Text) is not string text || !text.All(char.IsAsciiDigit) || text.Length > 2)
        {
            e.CancelCommand();
        }
    }

    // Two typed digits are a complete hour or minute.
    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.IsKeyboardFocusWithin && box.Text.Length == 2)
        {
            Commit(box);
        }
    }

    private void OnBoxFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();

    private void OnBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit((TextBox)sender);

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var hour = ReferenceEquals(box, HourBox);
        switch (e.Key)
        {
            case Key.Up or Key.Down:
                Commit(box);
                Step(hour ? TimeSpan.FromHours(e.Key == Key.Up ? 1 : -1) : TimeSpan.FromMinutes(e.Key == Key.Up ? 1 : -1));
                box.SelectAll();
                e.Handled = true;
                break;
            case Key.Enter:
                Commit(box);
                box.SelectAll();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Takes the typed digits as the new hour or minute; anything out of range is clamped, anything else restored.</summary>
    internal void Commit(TextBox box)
    {
        var hour = ReferenceEquals(box, HourBox);
        if (!int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var typed))
        {
            Render(animate: false);
            return;
        }

        var value = hour ? Math.Clamp(typed, 0, 23) : Math.Clamp(typed, 0, 59);
        var next = hour ? new TimeSpan(value, Value.Minutes, 0) : new TimeSpan(Value.Hours, value, 0);
        if (next != Value)
        {
            Value = next;
        }
        else
        {
            Render(animate: false);
        }
    }
}
