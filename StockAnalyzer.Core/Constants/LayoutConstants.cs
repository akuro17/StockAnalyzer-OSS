namespace StockAnalyzer.Core.Constants;

/// <summary>
/// Centralized layout constants.
/// </summary>
public static class LayoutConstants
{
    // Chart Layout Ratios
    public const double ChartHeightRatio = 0.8;
    public const double VolumeHeightRatio = 0.2;
    
    // Margins
    public const float ChartMarginTop = 30f;
    public const float ChartMarginBottom = 30f;
    public const float ChartMarginHorizontal = 50f;
    
    // Default Sizes
    public const float ThinStrokeWidth = 0.5f;
    public const float DefaultStrokeWidth = 1.0f;
    public const float ThickStrokeWidth = 2.0f;
    
    // Text Sizes
    public const float AxisFontSize = 12f;
    public const float LabelFontSize = 12f;

    // Z-Score Multi-Symbol Parameters
    public const decimal ZScoreMinRange = 8.0m; // Ensure ±4.0σ range for clipping visibility

    // Window Tear-off Constraints
    public const int MinDetachedWindowWidth = 200;
    public const int MinDetachedWindowHeight = 200;

    // Panel Chart Management
    public const string PanelChartIdPrefix = "PanelChart_";
    public const int MaxPanelTabs = 16;

    // Added for Step 80-6-1:
    public const double MinPanelHeight = 50.0;
    public const double DefaultLeftWidth = 200.0;
    public const double DefaultRightWidth = 250.0;
    public const double DefaultBottomHeight = 250.0;
    public const double DefaultTopHeight = 0.0;
    public const double ScreenerLabelColumnWidth = 260.0;

    // Layout Clamping Boundaries
    public const double MinPanelWidthClamp = 50.0;
    public const double MaxPanelWidthClamp = 2000.0;
    public const double MinPanelHeightClamp = 0.0;
    public const double MaxPanelHeightClamp = 1000.0;

    public const string DefaultWorkspaceFileName = "default_workspace.json";
    public const string TemporaryWorkspaceExtension = ".tmp";
    public const string BackupWorkspaceExtension = ".bak";

    // Added for Step 2.5:
    public const int DEFAULT_PANEL_WIDTH = 300;
    public const int DEFAULT_PANEL_HEIGHT = 150;
    public const int MIN_PANEL_WIDTH = 150;
    public const int MAX_PANEL_WIDTH = 800;
    public const int MIN_PANEL_HEIGHT = 75;
    public const int MAX_PANEL_HEIGHT = 600;
    public const int MAX_TABS_PER_PANEL = 20;
    public const int MAX_TAB_REORDER_DISTANCE = 100;

    // Default Detached Window Geometry Fallbacks
    public const double DefaultDetachedWindowX = 100.0;
    public const double DefaultDetachedWindowY = 100.0;
    public const double DefaultDetachedWindowWidth = 800.0;
    public const double DefaultDetachedWindowHeight = 600.0;

    // Dynamic Portfolio Constraints & Timers
    public const int PortfolioRefreshIntervalSeconds = 60;
    public const int PortfolioJitterMinMs = 1500;
    public const int PortfolioJitterMaxMs = 4500;
    
    public const int AllocationRefreshIntervalSeconds = 60;
    public const int AllocationJitterMinMs = 1000;
    public const int AllocationJitterMaxMs = 3000;
    
    public const int HeatmapJitterMinMs = 2000;
    public const int HeatmapJitterMaxMs = 5000;

    // Dynamic Portfolio & Allocation Audit Remediations (SSoT)
    public const decimal AllocationOthersThreshold = 0.03m;
    public const string CategoryCash = "Cash";
    public const string CategoryEquity = "Equity";
    public const string CategoryUnknown = "Unknown";
    public const uint ColorCash = 0xFF808080;
    public const uint ColorEquity = 0xFF1E90FF;
    public const uint ColorOthers = 0xFF708090;
    public const string PortfolioSummaryRootNodeDefaultName = "Portfolios";
    public static readonly System.Guid PortfolioSummaryRootNodeId = System.Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly uint[] AllocationSegmentPalette = new uint[]
    {
        0xFF1E90FF, // DodgerBlue
        0xFFDC143C, // Crimson
        0xFF228B22, // ForestGreen
        0xFFDAA520, // Goldenrod
        0xFF9370DB, // MediumPurple
        0xFFFF8C00, // DarkOrange
        0xFF008080, // Teal
        0xFFFF1493, // DeepPink
        0xFF7B68EE, // MediumSlateBlue
        0xFFA52A2A  // Brown
    };

    // Filter Settings Selector layout width SSoT (Rule #18)
    public const double FilterDialogMetricSelectorWidth = 220.0;
}

