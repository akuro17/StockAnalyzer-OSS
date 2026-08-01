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
