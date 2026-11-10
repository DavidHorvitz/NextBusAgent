using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BusPulse.ViewModels;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace BusPulse.Views;

/// <summary>Wiring only: hosts the three tab views in the NavigationView and keeps it in step with <see cref="ShellViewModel"/>.</summary>
public partial class ShellView : UserControl, INavigationViewPageProvider
{
    private static readonly Type[] PageTypes = [typeof(StopsView), typeof(TripPlannerView), typeof(LinesView)];

    private readonly Dictionary<Type, FrameworkElement> _pages = [];
    private ShellViewModel? _shell;
    private bool _syncing;

    public ShellView()
    {
        InitializeComponent();
        Nav.SetPageProviderService(this);
        DataContextChanged += (_, _) => Attach();
        Nav.Navigated += (_, args) =>
        {
            var index = Array.IndexOf(PageTypes, args.Page?.GetType());
            if (_shell is not null && index >= 0 && !_syncing)
            {
                _shell.SelectedTabIndex = index;
            }
        };
        Loaded += (_, _) => ShowSelectedPage();
    }

    public object? GetPage(Type pageType)
    {
        if (_shell is null)
        {
            return null;
        }

        if (!_pages.TryGetValue(pageType, out var page))
        {
            page = (FrameworkElement)Activator.CreateInstance(pageType)!;
            page.DataContext = pageType.Name switch
            {
                nameof(StopsView) => _shell.Stops,
                nameof(TripPlannerView) => _shell.TripPlanner,
                _ => _shell.Lines
            };
            page.Margin = new Thickness(16, 8, 16, 0);
            _pages[pageType] = page;
        }

        return page;
    }

    private void Attach()
    {
        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
        }

        _shell = DataContext as ShellViewModel;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
        }
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedTabIndex))
        {
            ShowSelectedPage();
        }
        else if (e.PropertyName == nameof(ShellViewModel.IsSettingsOpen) && _shell is { IsSettingsOpen: true })
        {
            SettingsPanel.Dispatcher.BeginInvoke(() => SettingsPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)));
        }
    }

    private void ShowSelectedPage()
    {
        if (_shell is null || !IsLoaded)
        {
            return;
        }

        var type = PageTypes[Math.Clamp(_shell.SelectedTabIndex, 0, PageTypes.Length - 1)];
        _syncing = true;
        try
        {
            Nav.Navigate(type);
        }
        finally
        {
            _syncing = false;
        }
    }
}
