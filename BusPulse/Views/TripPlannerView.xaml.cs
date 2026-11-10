using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ListBox = System.Windows.Controls.ListBox;
using BusPulse.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace BusPulse.Views;

public partial class TripPlannerView : System.Windows.Controls.UserControl
{
    public TripPlannerView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is TripPlannerViewModel previous)
            {
                previous.SearchFocusRequested -= OnSearchFocusRequested;
            }

            if (e.NewValue is TripPlannerViewModel current)
            {
                current.SearchFocusRequested += OnSearchFocusRequested;
            }
        };
    }

    private TripPlannerViewModel? ViewModel => DataContext as TripPlannerViewModel;

    // The tab may still be switching in when the request arrives, so focus after pending layout work.
    private void OnSearchFocusRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);

    private static void MoveHighlight(ListBox list, int delta)
    {
        if (list.Items.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(list.SelectedIndex + delta, delta < 0 ? -1 : 0, list.Items.Count - 1);
        list.SelectedIndex = index;
        if (index >= 0)
        {
            list.ScrollIntoView(list.Items[index]);
        }
    }

    // Down/Up move the highlight, Enter takes it (or looks the text up when nothing is highlighted), Esc closes the list.
    private void HandleKey(KeyEventArgs e, ListBox list, ICommand commit, ICommand close)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveHighlight(list, 1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveHighlight(list, -1);
                e.Handled = true;
                break;
            case Key.Escape:
                close.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                commit.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            HandleKey(e, SuggestionList, vm.CommitDestinationCommand, vm.ClearSuggestionsCommand);
        }
    }

    private void OriginSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            HandleKey(e, OriginSuggestionList, vm.CommitOriginCommand, vm.ClearOriginSuggestionsCommand);
        }
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        SearchBox.SelectAll();
        ViewModel?.ShowDefaultSuggestions(origin: false);
    }

    private void OriginSearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        OriginSearchBox.SelectAll();
        ViewModel?.ShowDefaultSuggestions(origin: true);
    }

    // Leaving the box looks the text up once, unless focus only moved into the list below it (a click on a row).
    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsInside(e.NewFocus as DependencyObject, SuggestionList))
        {
            ViewModel?.LeaveDestinationCommand.Execute(null);
        }
    }

    private void OriginSearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsInside(e.NewFocus as DependencyObject, OriginSuggestionList))
        {
            ViewModel?.LeaveOriginCommand.Execute(null);
        }
    }

    private void SuggestionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom(SuggestionList, e.OriginalSource) is { } item)
        {
            ViewModel?.ChooseSuggestionCommand.Execute(item);
        }
    }

    private void OriginSuggestionList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemFrom(OriginSuggestionList, e.OriginalSource) is { } item)
        {
            ViewModel?.ChooseOriginSuggestionCommand.Execute(item);
        }
    }

    private static object? ItemFrom(ListBox list, object source) =>
        source is DependencyObject element && ItemsControl.ContainerFromElement(list, element) is ListBoxItem container
            ? container.DataContext
            : null;

    private static bool IsInside(DependencyObject? element, DependencyObject parent)
    {
        for (var current = element; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, parent))
            {
                return true;
            }
        }

        return false;
    }
}
