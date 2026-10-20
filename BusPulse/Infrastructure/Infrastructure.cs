using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using BusPulse.Configuration;
using Microsoft.Extensions.Logging;
using Application = System.Windows.Application;

namespace BusPulse.Infrastructure;

/// <summary>The single place where work is marshalled onto the UI thread.</summary>
public interface IDispatcher
{
    void Post(Action action);
}

public sealed class WpfDispatcher : IDispatcher
{
    public void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}

public interface ILocalizer
{
    string Get(string key);

    void SetLanguage(string language);

    /// <summary>Raised after the language changed, so text captured earlier can be refreshed.</summary>
    event EventHandler? LanguageChanged;
}

public sealed class ResourceLocalizer : ILocalizer
{
    public event EventHandler? LanguageChanged;

    public string Get(string key) => Application.Current?.TryFindResource(key) as string ?? key;

    public void SetLanguage(string language)
    {
        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{language}.xaml", UriKind.Relative)
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.RemoveAt(0);
        dictionaries.Insert(0, dictionary);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}

public interface ISettingsService
{
    AppConfig Config { get; }

    /// <summary>Persists the settings; returns false (and logs) instead of throwing on storage errors.</summary>
    Task<bool> SaveAsync();
}

public sealed class SettingsService(AppConfig config, AppConfigStore store, ILogger<SettingsService> logger) : ISettingsService
{
    public AppConfig Config => config;

    public async Task<bool> SaveAsync()
    {
        try
        {
            await store.SaveAsync(config);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or InvalidDataException)
        {
            logger.LogError(exception, "Could not save BusPulse settings.");
            return false;
        }
    }
}

/// <summary>Saved places shared by the trip planner and the settings tab.</summary>
public sealed class SavedPlaceCollection(ISettingsService settings)
{
    public ObservableCollection<SavedPlace> Items { get; } = new(settings.Config.SavedPlaces);

    public void SyncToConfig() => settings.Config.SavedPlaces = Items.ToList();
}
