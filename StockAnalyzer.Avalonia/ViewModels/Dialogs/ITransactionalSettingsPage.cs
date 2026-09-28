using System.Threading.Tasks;

namespace StockAnalyzer.Avalonia.ViewModels.Dialogs;

/// <summary>Optional save/close contract for settings pages with a live external preview.</summary>
public interface ITransactionalSettingsPage
{
    bool IsSaveInProgress { get; }
    Task<bool> TrySaveChangesAsync();
    void RestoreSavedPreviewOnClose();
}
