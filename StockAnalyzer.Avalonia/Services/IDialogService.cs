using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Models;

namespace StockAnalyzer.Avalonia.Services;

public interface IMultiSyncProgressSession : System.IDisposable
{
    void Show(object? owner = null);
    void Close();
    MultiSyncProgressViewModel ViewModel { get; }
}



public interface IDialogService
{
    Task ShowAlertAsync(string title, string message);
    Task<bool> ShowConfirmationAsync(string title, string message);
    Task<string?> ShowInputAsync(string title, string message, string defaultValue = "");
    Task<AddTickerResult> ShowAddTickerDialogAsync(Guid targetProfileId);
    Task<Transaction?> ShowEditTransactionDialogAsync(EditTransactionDialogViewModel viewModel);
    Task<(string Text, double FontSize)?> ShowTextDialogAsync(string title, string defaultText = "", double defaultFontSize = 12);
    Task<DrawingSettingsResult> ShowDrawingSettingsDialogAsync(StockAnalyzer.Avalonia.Drawing.IChartObject drawing);
    Task<Color?> ShowColorPickerAsync(Color initialColor);
    Task ShowIndicatorSettingsDialogAsync(IEnumerable<CoreIndicatorSettings> currentIndicators, Action<IEnumerable<CoreIndicatorSettings>>? onApply = null);
    Task ShowIndicatorPropertiesDialogAsync(CoreIndicatorSettings indicator, Action<CoreIndicatorSettings>? onApply = null, IEnumerable<CoreIndicatorSettings>? allIndicators = null);
    Task ShowThemeSettingsDialogAsync();
    Task ShowSettingsDialogAsync(string? initialCategoryKey = null);
    Task<List<string>?> ShowColumnChooserDialogAsync(IEnumerable<WatchlistColumnMetadata> allColumns, IEnumerable<string> activeColumns, Action<List<string>>? onApply = null);
    Task<StockAnalyzer.Core.Models.Settings.FilterSettings?> ShowFilterSettingsDialogAsync(
        StockAnalyzer.Core.Models.Settings.FilterSettings initialSettings,
        Action<StockAnalyzer.Core.Models.Settings.FilterSettings>? onApply = null);
    Task ShowFilterTemplatePickerDialogAsync(
        StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner,
        StockAnalyzer.Avalonia.ViewModels.TickerList.FilterNode targetNode);
    Task ShowFilterTemplatePickerForNewFilterDialogAsync(
        StockAnalyzer.Avalonia.ViewModels.TickerListViewModel owner,
        StockAnalyzer.Avalonia.ViewModels.TickerList.TickerGroupNode parentNode);
    Task ShowScreenerDialogAsync();
    Task<BulkTagEditResult?> ShowBulkTagEditDialogAsync(IEnumerable<string> existingTags);
    Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? longVal = null, decimal? exitLong = null, decimal? stopLossLong = null, decimal? shortVal = null, decimal? exitShort = null, decimal? stopLossShort = null, string? reminder = null, Action<decimal?, decimal?, decimal?, decimal?, decimal?, decimal?, string?>? onSave = null);
    [System.Obsolete("Use the 6-parameter overload (longVal, exitLong, stopLossLong, shortVal, exitShort, stopLossShort, reminder) instead.")]
    Task<bool> ShowEditTickerNotesDialogAsync(string ticker, decimal? entryPrice, decimal? targetPrice, decimal? stopLoss, string? reminder, Action<decimal?, decimal?, decimal?, string?>? onSave);
    Task ShowNoteTrashDialogAsync(StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab initialTab = StockAnalyzer.Avalonia.ViewModels.Notes.NoteTrashInitialTab.Deleted);
    IMultiSyncProgressSession CreateMultiSyncProgressSession();

    Task<StockAnalyzer.Core.Services.PythonSetupDecision> ShowPythonSetupConfirmationAsync();
    Task ShowManualSetupInstructionsAsync();
    Task<StockAnalyzer.Core.Services.PythonSetupDecision> ShowPythonUpdateConfirmationAsync();
    Task ShowPythonManualUpdateInstructionsAsync();
    Task RunWithProgressAsync(string title, System.Func<System.IProgress<string>, Task> action);

    object? GetMainWindowOwner();
    Task ShowLogViewerAsync();
    
    // File dialogs
    Task<string?> ShowOpenFileDialogAsync(string title, string[]? filters = null);
    Task<string?> ShowSaveFileDialogAsync(string title, string defaultExtension = "", string defaultFilename = "", string[]? filters = null, string? initialDirectory = null);
    Task<string?> ShowOpenFolderDialogAsync(string title, string? initialDirectory = null);
    Task<bool> ShowExportChartImageDialogAsync(ChartViewModel chartViewModel);
    void Shutdown();
    void ActivateMainWindow();
}

public enum DrawingSettingsResult
{
    None,
    Changed,
    Deleted
}
