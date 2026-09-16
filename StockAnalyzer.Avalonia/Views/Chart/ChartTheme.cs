namespace StockAnalyzer.Avalonia.Views.Chart;

/// <summary>
/// Centralized theme and layout constants for chart rendering.
/// </summary>
public static class ChartTheme
{
    #region Layout Margins
    
    /// <summary>
    /// Left margin for chart area.
    /// </summary>
    public static readonly float MarginLeft = StockAnalyzer.Core.ChartConstants.MarginLeft;
    
    /// <summary>
    /// Right margin for chart area (wide enough for Y-axis price labels).
    /// </summary>
    public static readonly float MarginRight = StockAnalyzer.Core.ChartConstants.MarginRight;
    
    /// <summary>
    /// Horizontal margin (legacy: used as left margin in some places).
    /// Prefer MarginLeft/MarginRight for new code.
    /// </summary>
    public static readonly float MarginHorizontal = StockAnalyzer.Core.ChartConstants.MarginHorizontal;
    
    /// <summary>
    /// Top margin for chart area.
    /// </summary>
    public static readonly float MarginTop = StockAnalyzer.Core.ChartConstants.MarginTop;
    
    /// <summary>
    /// Bottom margin for chart area.
    /// </summary>
    public static readonly float MarginBottom = StockAnalyzer.Core.ChartConstants.MarginBottom;
    
    #endregion

    #region Crosshair Settings

    /// <summary>
    /// Padding for crosshair labels.
    /// </summary>
    public const float CrosshairLabelPadding = 4f;

    /// <summary>
    /// Height of crosshair labels.
    /// </summary>
    public const float CrosshairLabelHeight = 20f;

    #endregion

    #region Reverse Watch Settings

    /// <summary>
    /// Margin for Reverse Watch chart area.
    /// </summary>
    public const float ReverseWatchMargin = 40f;

    /// <summary>
    /// Radius for Reverse Watch data points.
    /// </summary>
    public const float ReverseWatchPointRadius = 3f;

    #endregion

    #region Grid Rendering

    /// <summary>
    /// Grid dash pattern: line length in pixels.
    /// </summary>
    public const float GridDashOn = 2f;

    /// <summary>
    /// Grid dash pattern: space length in pixels.
    /// </summary>
    public const float GridDashOff = 2f;

    /// <summary>
    /// Font size for grid labels (pixels).
    /// </summary>
    public const float GridLabelTextSize = 10f;

    /// <summary>
    /// Target number of horizontal grid lines for main chart.
    /// </summary>
    public const decimal HorizontalGridLineTarget = 8m;

    /// <summary>
    /// Pixel interval between vertical grid lines.
    /// </summary>
    public const float VerticalGridPixelInterval = 120f;

    /// <summary>
    /// Target number of horizontal grid lines for indicator panels.
    /// </summary>
    public const decimal PanelHorizontalGridLineTarget = 4m;

    /// <summary>
    /// Minimum range threshold to show a middle grid line in fixed-range panels.
    /// </summary>
    public const decimal FixedRangeMiddleLineThreshold = 10m;

    /// <summary>
    /// Horizontal offset for grid labels from panel right edge (pixels).
    /// </summary>
    public const float GridLabelOffsetX = 5f;

    /// <summary>
    /// Vertical offset for grid labels from grid line (pixels).
    /// </summary>
    public const float GridLabelOffsetY = 4f;

    /// <summary>
    /// Alpha value for the Z-Score 0.0 baseline (0-255).
    /// </summary>
    public const byte ZScoreZeroAlpha = 200;

    /// <summary>
    /// Alpha value for Z-Score threshold lines (±1, ±2) (0-255).
    /// </summary>
    public const byte ZScoreThresholdAlpha = 140;

    /// <summary>
    /// Grid dash pattern for Z-Score thresholds: line length in pixels.
    /// </summary>
    public const float ZScoreThresholdDashOn = 6f;

    /// <summary>
    /// Grid dash pattern for Z-Score thresholds: space length in pixels.
    /// </summary>
    public const float ZScoreThresholdDashOff = 4f;

    /// <summary>
    /// Line width for the Z-Score 0.0 baseline.
    /// </summary>
    public const float ZScoreZeroWidth = 2.0f;

    #endregion

    #region Axis Label Settings (Y-Axis Label Registry)

    /// <summary>
    /// Height of axis projection labels (pixels).
    /// </summary>
    public const float AxisLabelHeight = 18f;

    /// <summary>
    /// Padding inside axis projection labels (pixels).
    /// </summary>
    public const float AxisLabelPadding = 4f;

    /// <summary>
    /// Corner radius for axis projection label rounded rectangles (pixels).
    /// </summary>
    public const float AxisLabelCornerRadius = 3f;

    /// <summary>
    /// Width of the arrow pointer on axis projection labels (pixels).
    /// </summary>
    public const float AxisLabelArrowWidth = 6f;

    /// <summary>
    /// Font size for axis projection label text (pixels).
    /// </summary>
    public const float AxisLabelFontSize = 11f;

    #endregion

    #region Chart Layout

    /// <summary>
    /// Height percentage of each indicator panel relative to total chart height.
    /// </summary>
    public const float PanelHeightPercentage = 0.15f;

    /// <summary>
    /// Gap between indicator panels (pixels).
    /// </summary>
    public const float PanelGap = 10f;

    /// <summary>
    /// Maximum ratio of total height that panels can occupy.
    /// </summary>
    public const double MaxPanelHeightRatio = 0.6;

    /// <summary>
    /// Height ratio of main chart area (when volume is shown).
    /// </summary>
    public const double MainChartHeightRatio = 0.8;

    /// <summary>
    /// Height ratio of volume area (when volume is shown).
    /// </summary>
    public const double VolumeChartHeightRatio = 0.2;

    #endregion

    #region Area Chart Fill

    /// <summary>
    /// Alpha value for the top of the area fill gradient (0-255).
    /// </summary>
    public const byte AreaFillAlphaTop = 100;

    /// <summary>
    /// Alpha value for the bottom of the area fill gradient (0-255).
    /// </summary>
    public const byte AreaFillAlphaBottom = 20;

    /// <summary>
    /// Level of Detail (LOD) threshold: Fill is hidden if candle width is less than this value.
    /// </summary>
    public const float AreaChartLODThreshold = 2.01f;

    /// <summary>
    /// Threshold to show line markers (circles) on the area chart line.
    /// </summary>
    public const float AreaChartMarkerThreshold = 5.0f;

    /// <summary>
    /// Base alpha scale (offset) for dark backgrounds.
    /// </summary>
    public const float AreaChartAlphaBaseScale = 0.65f;

    /// <summary>
    /// Intensity of luminance response for alpha scaling.
    /// </summary>
    public const float AreaChartAlphaLuminanceScale = 0.35f;

    /// <summary>
    /// Alpha value for the horizontal price projection line (0-255).
    /// </summary>
    public const byte AreaChartProjectionAlpha = 128;

    #endregion

    #region Axis Occlusion

    /// <summary>
    /// Buffer distance (pixels) to hide grid labels when they approach priority badges.
    /// </summary>
    public const float AxisLabelOcclusionBuffer = 5.0f;

    #endregion
}
