using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using BusPulse.ViewModels;

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

    // Keyboard navigation for the destination search: Down enters the list, Esc clears, Enter picks the first match.
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when SuggestionList.Items.Count > 0:
                SuggestionList.SelectedIndex = 0;
                (SuggestionList.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                ViewModel?.ClearSuggestionsCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when SuggestionList.Items.Count > 0:
                ViewModel?.ChooseSuggestionCommand.Execute(SuggestionList.Items[0]);
                e.Handled = true;
                break;
            case Key.Enter when SearchBox.Text.Trim().Length > 0:
                // Nothing local matches: Enter is an explicit request to look the text up as an address.
                ViewModel?.SearchDestinationAddressCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    // The origin search box works the same way as the destination one.
    private void OriginSearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when OriginSuggestionList.Items.Count > 0:
                OriginSuggestionList.SelectedIndex = 0;
                (OriginSuggestionList.ItemContainerGenerator.ContainerFromIndex(0) as System.Windows.Controls.ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                ViewModel?.ClearOriginSuggestionsCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter when OriginSuggestionList.Items.Count > 0:
                ViewModel?.ChooseOriginSuggestionCommand.Execute(OriginSuggestionList.Items[0]);
                e.Handled = true;
                break;
            case Key.Enter when OriginSearchBox.Text.Trim().Length > 0:
                ViewModel?.SearchOriginAddressCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OriginSuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && OriginSuggestionList.SelectedItem is not null)
        {
            ViewModel?.ChooseOriginSuggestionCommand.Execute(OriginSuggestionList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.ClearOriginSuggestionsCommand.Execute(null);
            OriginSearchBox.Focus();
            e.Handled = true;
        }
    }

    private void OriginSuggestionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (OriginSuggestionList.SelectedItem is not null)
        {
            ViewModel?.ChooseOriginSuggestionCommand.Execute(OriginSuggestionList.SelectedItem);
        }
    }

    private void SuggestionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestionList.SelectedItem is not null)
        {
            ViewModel?.ChooseSuggestionCommand.Execute(SuggestionList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ViewModel?.ClearSuggestionsCommand.Execute(null);
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void SuggestionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SuggestionList.SelectedItem is not null)
        {
            ViewModel?.ChooseSuggestionCommand.Execute(SuggestionList.SelectedItem);
        }
    }
}
