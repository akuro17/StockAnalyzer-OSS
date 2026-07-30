using System;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.ViewModels;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// A placeholder ViewModel for settings pages that are not yet implemented.
/// </summary>
public class PlaceholderSettingsViewModel : ViewModelBase, ISettingsPageViewModel
{
    public string TitleKey { get; }
    public string IconKey { get; } = "SettingsIcon";
    public bool IsModified => false;

    public PlaceholderSettingsViewModel(string titleKey = "Settings_General")
    {
        TitleKey = titleKey;
    }

    public Task SaveChangesAsync() => Task.CompletedTask;
    public void RevertChanges() { }
    public void ResetToDefault() { }
}
