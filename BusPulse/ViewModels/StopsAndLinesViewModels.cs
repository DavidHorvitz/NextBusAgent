using BusPulse.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BusPulse.ViewModels;

public sealed partial class LinesViewModel(ILocalizer localizer) : ObservableObject
{
    public string EmptyMessage => localizer.Get("LinesEmpty");
}
