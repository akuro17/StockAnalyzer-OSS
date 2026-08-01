using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Settings;

/// <summary>
/// Represents the root settings object for an entire workspace profile.
/// This includes global settings like the active theme and the list of active indicators.
/// </summary>
public class WorkspaceSettings
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Default Profile";
    public DateTime LastModified { get; set; } = DateTime.Now;

    /// <summary>
    /// Global theme settings key-value pair, stored decoupled from Avalonia to allow Core serialization.
    /// Values are typically hex strings, e.g. "ChartBackground":"#FAEABB".
    /// </summary>
    public Dictionary<string, string> ThemeSettings { get; set; } = new();

    /// <summary>
    /// The list of active indicators configured in this workspace.
    /// </summary>
    public List<CoreIndicatorSettings> Indicators { get; set; } = new();

    /// <summary>
    /// Workspace layout properties
    /// </summary>
    public double LeftPanelWidth { get; set; } = 200;
    public double RightPanelWidth { get; set; } = 200;
    public double TopPanelHeight { get; set; } = 200;
    public double BottomPanelHeight { get; set; } = 200;
    public bool IsLeftPanelVisible { get; set; } = true;
    public bool IsRightPanelVisible { get; set; } = true;
    public bool IsTopPanelVisible { get; set; } = false;
    public bool IsBottomPanelVisible { get; set; } = false;
    public bool IsLeftPanelPinned { get; set; } = true;
    public bool IsRightPanelPinned { get; set; } = true;
    public bool IsTopPanelPinned { get; set; } = true;
    public bool IsBottomPanelPinned { get; set; } = true;

    /// <summary>
    /// Stores the selected tab index for each panel (Left, Right, Top, Bottom).
    /// </summary>
    public Dictionary<string, int> PanelSelectedTabIndices { get; set; } = new();

    /// <summary>
    /// Stores the ordered list of tab IDs for each panel (Left, Right, Top, Bottom).
    /// </summary>
    public Dictionary<string, List<string>> PanelTabIds { get; set; } = new();

    /// <summary>
    /// Stores the active ticker symbol.
    /// </summary>
    public string? SelectedTicker { get; set; }

    /// <summary>
    /// Stores the active timeframe (e.g., "Daily", "Weekly").
    /// </summary>
    public string? SelectedTimeframe { get; set; }

    /// <summary>
    /// Stores the maximum number of candles to load (Period).
    /// </summary>
    public int MaxCandleCount { get; set; } = 200;

    public string? ChartType { get; set; }
    public bool IsMainWindowVisible { get; set; } = true;
    public bool IsSubWindowVisible { get; set; } = true;

    /// <summary>
    /// Whether metadata sync is enabled by default in the sync window.
    /// </summary>
    public bool IsMetadataSyncEnabled { get; set; } = true;

    /// <summary>
    /// Whether time series sync is enabled by default in the sync window.
    /// </summary>
    public bool IsTimeSeriesSyncEnabled { get; set; } = true;

    /// <summary>
    /// Whether auto sync is enabled by default in the sync window.
    /// </summary>
    public bool IsAutoSyncEnabled { get; set; } = true;

    /// <summary>
    /// Whether full history sync is enabled by default in the sync window.
    /// </summary>
    public bool IsFullHistoryEnabled { get; set; } = false;

    /// <summary>
    /// Synchronization delay between tickers in seconds.
    /// </summary>
    public decimal SyncDelaySeconds { get; set; } = 3.0m;

    /// <summary>
    /// Synchronization minimum delay between tickers in seconds.
    /// </summary>
    public decimal SyncDelayMinSeconds { get; set; } = 3.0m;

    /// <summary>
    /// Synchronization maximum delay between tickers in seconds.
    /// </summary>
    public decimal SyncDelayMaxSeconds { get; set; } = 5.0m;

    /// <summary>
    /// Number of years to synchronize when full history is disabled.
    /// </summary>
    public int StartSyncPeriodYears { get; set; } = 5;

    /// <summary>
    /// Sidebar width in the TickerList (Watchlist) panel.
    /// </summary>
    public double TickerListSidebarWidth { get; set; } = 200;

    /// <summary>
    /// The list of visible columns in the TickerList (member names).
    /// </summary>
    public List<string> TickerListVisibleColumns { get; set; } = new();

    /// <summary>
    /// The list of visible columns in the Screener Results grid (member names).
    /// </summary>
    public List<string> ScreenerResultsVisibleColumns { get; set; } = new();

    /// <summary>
    /// The ID of the currently selected watchlist.
    /// </summary>
    public Guid? SelectedWatchlistId { get; set; }

    /// <summary>
    /// The member name of the column currently used for sorting in the TickerList.
    /// </summary>
    public string? TickerListSortColumn { get; set; }

    /// <summary>
    /// The direction of sorting (None=0, Ascending=1, Descending=2).
    /// </summary>
    public int TickerListSortDirection { get; set; }

    /// <summary>
    /// The list of user-defined watchlist profiles.
    /// </summary>
    public List<StockAnalyzer.Core.Models.Watchlist.WatchlistProfile> WatchlistProfiles { get; set; } = new()
    {
        new StockAnalyzer.Core.Models.Watchlist.WatchlistProfile(Guid.NewGuid(), "Default", IndicatorColor.Gray)
    };

    /// <summary>
    /// The list of currently detached tabs and their location metadata.
    /// </summary>
    public List<DetachedTabInfo> DetachedTabs { get; set; } = new();

    /// <summary>
    /// Configuration data for panel charts, such as sync toggles and local configurations.
    /// </summary>
    public List<PanelChartSettings> PanelCharts { get; set; } = new();

    /// <summary>
    /// Root-level user-defined tag filters.
    /// </summary>
    public List<FilterSettings> TagFilters { get; set; } = new();
}

/// <summary>
/// Simple DTO for persisting detached tab state.
/// </summary>
public class DetachedTabInfo
{
    public string TabId { get; set; } = string.Empty;
    public string? OriginalPanelName { get; set; }
    
    /// <summary>
    /// The ID of the detached window container this tab belongs to.
    /// Tabs with the same ContainerId will be grouped together upon restoration.
    /// </summary>
    public string? ContainerId { get; set; }
    
    /// <summary>
    /// Whether the tab was an independent window (factory-spawned).
    /// </summary>
    public bool IsIndependent { get; set; }

    /// <summary>
    /// Whether this tab synchronizes its state with the main active chart.
    /// </summary>
    public bool IsSyncEnabled { get; set; } = true;
    
    /// <summary>
    /// State properties for independent or non-synced chart windows
    /// </summary>
    public string? Symbol { get; set; }
    public string? Timeframe { get; set; }
    public int MaxCandleCount { get; set; }

    public double X { get; set; } = double.NaN;
    public double Y { get; set; } = double.NaN;
    public double Width { get; set; } = double.NaN;
    public double Height { get; set; } = double.NaN;

    public string? ChartType { get; set; }
    public bool IsMainWindowVisible { get; set; } = true;
    public bool IsSubWindowVisible { get; set; } = true;

    /// <summary>
    /// Saved indicator settings for the window.
    /// </summary>
    public List<StockAnalyzer.Core.Models.CoreIndicatorSettings> Indicators { get; set; } = new();
}

/// <summary>
/// Persists individual settings for internal panel chart tabs.
/// </summary>
public class PanelChartSettings
{
    public string TabId { get; set; } = string.Empty;
    public bool IsSyncEnabled { get; set; } = true;
    public string? Symbol { get; set; }
    public string? Timeframe { get; set; }
    public int MaxCandleCount { get; set; }
    public string? ChartType { get; set; }
    public bool IsMainWindowVisible { get; set; } = true;
    public bool IsSubWindowVisible { get; set; } = true;
    public List<StockAnalyzer.Core.Models.CoreIndicatorSettings> Indicators { get; set; } = new();
}
