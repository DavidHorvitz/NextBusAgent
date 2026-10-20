using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using BusPulse.ViewModels;

namespace BusPulse.Views;

public partial class TripPlannerView : System.Windows.Controls.UserControl
{
    public TripPlannerView()
    {
        InitializeComponent();
    }

    private TripPlannerViewModel? ViewModel => DataContext as TripPlannerViewModel;

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
