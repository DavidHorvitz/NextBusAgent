using BusPulse.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BusPulse.ViewModels;

/// <summary>Nearby stops. Location and nearby-stop lookup arrive with the stops tab implementation.</summary>
public sealed partial class StopsViewModel(ILocalizer localizer) : ObservableObject
{
    public string EmptyMessage => localizer.Get("StopsEmpty");
}

public sealed partial class LinesViewModel(ILocalizer localizer) : ObservableObject
{
    public string EmptyMessage => localizer.Get("LinesEmpty");
}
