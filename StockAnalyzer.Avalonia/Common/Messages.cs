using System.Collections.Generic;
using CommunityToolkit.Mvvm.Messaging.Messages;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Confluence;
using StockAnalyzer.Core.Models.Indicators;

namespace StockAnalyzer.Avalonia.Common;

public class IndicatorSettingsChangedMessage : ValueChangedMessage<List<CoreIndicatorSettings>>
{
    public IndicatorSettingsChangedMessage(List<CoreIndicatorSettings> value) : base(value)
    {
    }
}

/// <summary>
/// Published by ChartDataCoordinator when a new snapshot is available.
/// ConfluenceDashboardViewModel subscribes to this to maintain Zero-Allocation sync.
/// </summary>
/// <summary>
/// Published by ChartDataCoordinator when a new snapshot is available.
/// ConfluenceDashboardViewModel subscribes to this to maintain Zero-Allocation sync.
/// </summary>
public class ConfluenceSnapshotUpdatedMessage 
{
    public IReadOnlyDictionary<string, IIndicatorResult>? FullIndicatorResults { get; }
    public IReadOnlyList<CoreIndicatorSettings> IndicatorSettings { get; }
    public int FullCount { get; }
    public ConfluenceSnapshotUpdatedMessage(
        IReadOnlyDictionary<string, IIndicatorResult>? fullResults, 
        IReadOnlyList<CoreIndicatorSettings> settings, 
        int fullCount)
    {
        FullIndicatorResults = fullResults;
        IndicatorSettings = settings;
        FullCount = fullCount;
    }
}

/// <summary>
/// Published when a single indicator's settings are changed via the Properties dialog.
/// The Value contains the updated CoreIndicatorSettings with the same Id as the original.
/// </summary>
public class SingleIndicatorSettingsChangedMessage : ValueChangedMessage<CoreIndicatorSettings>
{
    public SingleIndicatorSettingsChangedMessage(CoreIndicatorSettings value) : base(value)
    {
    }
}

/// <summary>
/// Published when the mouse cursor moves over the chart, carrying the hovered candle data.
/// Subscribers (e.g., future Data Window) can react to crosshair position changes
/// without direct coupling to ChartBaseControl.
/// </summary>
public class CrosshairPositionChangedMessage : ValueChangedMessage<CrosshairPositionData>
{
    public CrosshairPositionChangedMessage(CrosshairPositionData value) : base(value)
    {
    }
}

/// <summary>
/// Data payload for crosshair position events.
/// This is a mutable class to support ZeroAllocation object pooling, preventing per-frame GC spikes on pointer move.
/// </summary>
public class CrosshairPositionData
{
    public int CandleIndex { get; set; }
    public CoreCandleData? HoveredCandle { get; set; }
    public string ChartSymbol { get; set; } = string.Empty;
    public int? ThreeLineBreakCount { get; set; }
    public StockAnalyzer.Core.Models.Analysis.ReverseWatchCurvePoint? ReverseWatchPoint { get; set; }
    public double? MouseY { get; set; }
    public ConfluenceResult? Confluence { get; set; }

    /// <summary>
    /// Screen X coordinate of the mouse pointer (relative to ChartCanvasBorder).
    /// Used by floating tooltip for position tracking.
    /// </summary>
    public double? ScreenX { get; set; }

    /// <summary>
    /// Screen Y coordinate of the mouse pointer (relative to ChartCanvasBorder).
    /// Used by floating tooltip for position tracking.
    /// </summary>
    public double? ScreenY { get; set; }
}

/// <summary>
/// Published when a chart display setting changes (e.g., ShowHeaderInfo, ShowPriceLabels).
/// Subscribers can react to rendering-relevant setting changes.
/// </summary>
public class ChartSettingsChangedMessage : ValueChangedMessage<ChartSettingChange>
{
    public ChartSettingsChangedMessage(ChartSettingChange value) : base(value)
    {
    }
}

/// <summary>
/// Data payload for chart setting change events.
/// </summary>
public record ChartSettingChange(
    string SettingName,
    object? NewValue
);

/// <summary>
/// Published when a ticker is selected in the Ticker List panel.
/// </summary>
public class TickerSelectedMessage : CommunityToolkit.Mvvm.Messaging.Messages.ValueChangedMessage<string>
{
    public TickerSelectedMessage(string value) : base(value)
    {
    }
}

/// <summary>
/// Published by TickerListViewModel when a bulk sync (MultiSync) completes for a specific symbol.
/// ChartViewModel subscribes to this to refresh the chart when the currently displayed symbol's data is updated.
/// This is distinct from TickerSelectedMessage (user selection) — it signals data availability.
/// </summary>
public class TickerDataRefreshedMessage : CommunityToolkit.Mvvm.Messaging.Messages.ValueChangedMessage<string>
{
    public TickerDataRefreshedMessage(string symbol) : base(symbol)
    {
    }
}

/// <summary>
/// Published when indicator calculations are completed in ChartViewModel.
/// </summary>
public class IndicatorResultsUpdatedMessage
{
}

/// <summary>
/// Published when a request to open indicator properties dialog is made.
/// </summary>
public class OpenIndicatorPropertiesMessage : ValueChangedMessage<CoreIndicatorSettings>
{
    public OpenIndicatorPropertiesMessage(CoreIndicatorSettings value) : base(value)
    {
    }
}
/// <summary>
/// Published when a layout or global preference has changed and requires persistence.
/// </summary>
public class LayoutChangedMessage { }

/// <summary>
/// Published by TearOffService to request removal of a tab from MainWindow panels.
/// Response indicates success.
/// </summary>
public class TearOffRequestMessage : RequestMessage<bool>
{
    public StockAnalyzer.Core.Models.UI.WorkspaceViewItem Item { get; }
    public TearOffRequestMessage(StockAnalyzer.Core.Models.UI.WorkspaceViewItem item) => Item = item;
}

/// <summary>
/// Published when a detached window is closing and requests restoration to the main window.
/// </summary>
public class RestoreRequestMessage : ValueChangedMessage<StockAnalyzer.Core.Models.UI.WorkspaceViewItem>
{
    public RestoreRequestMessage(StockAnalyzer.Core.Models.UI.WorkspaceViewItem value) : base(value) { }
}

public class CloseDetachedWindowMessage : ValueChangedMessage<string>
{
    public CloseDetachedWindowMessage(string containerId) : base(containerId) { }
}

/// <summary>
/// Request for the current active/main ticker symbol.
/// Used by sub-charts to synchronize state when sync is enabled.
/// </summary>
public class CurrentTickerRequestMessage : RequestMessage<string>
{
}

/// <summary>
/// Published when a new tab is added directly to a detached window.
/// Used to register the new tab in the global workspace manager for persistence.
/// </summary>
public class RegisterDetachedTabMessage : CommunityToolkit.Mvvm.Messaging.Messages.ValueChangedMessage<StockAnalyzer.Core.Models.UI.WorkspaceViewItem>
{
    public RegisterDetachedTabMessage(StockAnalyzer.Core.Models.UI.WorkspaceViewItem value) : base(value) { }
}

/// <summary>
/// Published when a chart's symbol is changed.
/// Used for updating UI elements like tab headers or window titles in a decoupled manner.
/// </summary>
public class ChartSymbolChangedMessage : CommunityToolkit.Mvvm.Messaging.Messages.ValueChangedMessage<string>
{
    public object Sender { get; }
    public ChartSymbolChangedMessage(object sender, string value) : base(value)
    {
        Sender = sender;
    }
}

/// <summary>
/// Published when a panel tab (WorkspaceViewItem) is permanently removed and its ViewModel
/// disposed. ViewSwitcher listens for this to evict the matching cached view from its
/// Zero-Object-Recreation cache, since a disposed ViewModel's view is otherwise held forever.
/// </summary>
public class WorkspaceViewItemRemovedMessage : CommunityToolkit.Mvvm.Messaging.Messages.ValueChangedMessage<object>
{
    public WorkspaceViewItemRemovedMessage(object viewModel) : base(viewModel)
    {
    }
}

/// <summary>
/// Published when the Column Chooser selection is applied.
/// </summary>
public class ColumnChooserAppliedMessage
{
    public List<string> ActiveColumns { get; }
    public ColumnChooserAppliedMessage(List<string> activeColumns)
    {
        ActiveColumns = activeColumns;
    }
}

/// <summary>
/// Published by a Note card's "チャートを見る" (view chart) action (spec section 6.2). The main
/// chart should switch to <see cref="Ticker"/>, load <see cref="Timeframe"/>, and once data is
/// available, jump to and center on <see cref="AnchorDate"/> via
/// <c>ChartViewModel.JumpToAnchorDate</c>.
/// </summary>
public class NoteChartJumpRequestedMessage
{
    public string Ticker { get; }
    public System.DateTime AnchorDate { get; }
    public TimeFrame Timeframe { get; }

    public NoteChartJumpRequestedMessage(string ticker, System.DateTime anchorDate, TimeFrame timeframe)
    {
        Ticker = ticker;
        AnchorDate = anchorDate;
        Timeframe = timeframe;
    }
}

/// <summary>
/// Published when the user clicks a Tickers-tab "Notes" cell: requests that the Notes tab
/// (NoteTimeline) be brought to the front and filtered to <see cref="Ticker"/>. Replaces the
/// column's former behavior of opening the Ticker Dashboard's Note editor.
/// </summary>
public class NavigateToNoteTimelineRequestedMessage
{
    public string Ticker { get; }

    public NavigateToNoteTimelineRequestedMessage(string ticker)
    {
        Ticker = ticker;
    }
}

/// <summary>
/// Published when the user clicks a Tickers-tab "Reminder" cell's "+" button: requests that the
/// Ticker Dashboard's Edit dialog open for <see cref="Ticker"/>. Lets the View layer (a cell
/// control) request the dialog without resolving <c>IDialogService</c> itself via a service
/// locator, mirroring <see cref="NavigateToNoteTimelineRequestedMessage"/>.
/// </summary>
public class OpenReminderDialogRequestedMessage
{
    public string Ticker { get; }

    public OpenReminderDialogRequestedMessage(string ticker)
    {
        Ticker = ticker;
    }
}

/// <summary>
/// Published by the app-startup orphaned-attachment scan (spec section 4.5) once, only when at
/// least one orphaned file was found. <see cref="MainWindowViewModel"/> surfaces a dismissible
/// status-bar notice; the full file list lives in
/// <c>OrphanedAttachmentScanResultHolder</c> for the trash dialog's "Orphaned Files" tab to read.
/// </summary>
public class OrphanedAttachmentsDetectedMessage
{
    public int Count { get; }

    public OrphanedAttachmentsDetectedMessage(int count)
    {
        Count = count;
    }
}
