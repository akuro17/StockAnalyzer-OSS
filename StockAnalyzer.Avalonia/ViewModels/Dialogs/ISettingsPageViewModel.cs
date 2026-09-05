using System.ComponentModel;
using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>
/// Defines the contract for a settings page hosted within the unified SettingsWindow.
/// </summary>
public interface ISettingsPageViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the localization key for the page title.
    /// </summary>
    string TitleKey { get; }

    /// <summary>
    /// Gets the resource key for the page icon.
    /// </summary>
    string IconKey { get; }

    /// <summary>
    /// Gets whether there are unsaved changes on this page.
    /// </summary>
    bool IsModified { get; }

    /// <summary>
    /// Persists the current changes to the underlying configuration.
    /// </summary>
    Task SaveChangesAsync();

    /// <summary>
    /// Reverts the current changes to the last saved state.
    /// </summary>
    void RevertChanges();

    /// <summary>
    /// Resets all settings on this page to their default values.
    /// </summary>
    void ResetToDefault();
}
