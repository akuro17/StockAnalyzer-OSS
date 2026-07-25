namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Static class containing default values for chart rendering constants.
/// These values serve as fallbacks when DI configuration is unavailable.
/// </summary>
public static class ChartConstants
{
    #region Chart Margins

    /// <summary>
    /// Default horizontal margin for chart drawing area (pixels).
    /// </summary>
    public const float DefaultChartMarginHorizontal = 20f;

    /// <summary>
    /// Default top margin for chart drawing area (pixels).
    /// </summary>
    public const float DefaultChartMarginTop = 35f;

    /// <summary>
    /// Default bottom margin for chart drawing area (pixels).
    /// </summary>
    public const float DefaultChartMarginBottom = 30f;

    #endregion

    #region Crosshair

    /// <summary>
    /// Default padding around crosshair labels (pixels).
    /// </summary>
    public const float DefaultLabelPadding = 4f;

    /// <summary>
    /// Default height of crosshair labels (pixels).
    /// </summary>
    public const float DefaultLabelHeight = 18f;

    #endregion

    #region ReverseWatch Renderer

    /// <summary>
    /// Default margin for ReverseWatch renderer (pixels).
    /// </summary>
    public const float DefaultReverseWatchMargin = 10f;

    /// <summary>
    /// Default point radius for data points (pixels).
    /// </summary>
    public const float DefaultPointRadius = 4f;

    /// <summary>
    /// Default radius for hovered points (pixels).
    /// </summary>
    public const float DefaultHoverPointRadius = 6f;

    /// <summary>
    /// Default distance threshold for hover detection (pixels).
    /// </summary>
    public const float DefaultHoverThresholdDistance = 15f;

    #endregion

    #region ReverseWatch Curve Window

    /// <summary>
    /// Default margin for ReverseWatch Curve Window (pixels).
    /// </summary>
    public const float DefaultCurveWindowMargin = 60f;

    /// <summary>
    /// Default height of time axis (pixels).
    /// </summary>
    public const float DefaultTimeAxisHeight = 25f;

    #endregion

    #region Drawing Constants

    /// <summary>
    /// Default padding around text boxes and callouts (pixels).
    /// </summary>
    public const float DefaultTextBackgroundPadding = 8f;

    /// <summary>
    /// Default padding around price labels (pixels).
    /// </summary>
    public const float DefaultPriceLabelPadding = 4f;

    /// <summary>
    /// Default corner radius for drawing object text backgrounds (pixels).
    /// </summary>
    public const float DefaultDrawingCornerRadius = 4f;

    /// <summary>
    /// Default hit test distance tolerance (pixels).
    /// </summary>
    public const double DefaultHitTestTolerance = 5.0;

    /// <summary>
    /// Default radius for drawing object handles (pixels).
    /// </summary>
    public const float DefaultHandleRadius = 4f;

    /// <summary>
    /// Default radius for selected drawing object handles (pixels).
    /// </summary>
    public const float SelectedHandleRadius = 5f;

    /// <summary>
    /// Angle Object: Default length of the horizontal reference line (pixels).
    /// </summary>
    public const float AngleHorizontalRefLength = 50f;

    /// <summary>
    /// Angle Object: Default offset for angle text (pixels).
    /// </summary>
    public const float AngleTextOffset = 10f;

    /// <summary>
    /// Angle Object: Default radius for the angle arc (pixels).
    /// </summary>
    public const float AngleArcRadius = 40f;

    /// <summary>
    /// Fibonacci Objects: Default horizontal offset for text labels (pixels).
    /// </summary>
    public const float FibonacciTextOffsetX = 5f;

    /// <summary>
    /// Fibonacci Objects: Default vertical offset for percentage text (pixels).
    /// </summary>
    public const float FibonacciTextOffsetYPercent = -3f;

    /// <summary>
    /// Fibonacci Objects: Default vertical offset for value text (pixels).
    /// </summary>
    public const float FibonacciTextOffsetYValue = 10f;

    /// <summary>
    /// Fibonacci/Channel Objects: Default artificial extreme coordinate for infinite extension (pixels).
    /// </summary>
    public const float FibonacciInfiniteExtensionExtremes = -1000f;

    /// <summary>
    /// Polyline Object: Default vertical offset for evenly indexed labels (pixels).
    /// </summary>
    public const float PolylineLabelOffsetYEven = -10f;

    /// <summary>
    /// Polyline Object: Default vertical offset for oddly indexed labels (pixels).
    /// </summary>
    public const float PolylineLabelOffsetYOdd = 20f;

    /// <summary>
    /// Pitchfork Object: Default padding to calculate safe clipping bounds (pixels).
    /// </summary>
    public const float PitchforkBoundsPadding = 50f;

    /// <summary>
    /// Arrow Object: Default size of the arrowhead (pixels).
    /// </summary>
    public const float ArrowHeadSize = 15f;

    /// <summary>
    /// Bar Pattern Object: Default visual body width for ghost candles (pixels).
    /// </summary>
    public const float BarPatternBodyWidth = 8f;

    /// <summary>
    /// Bar Pattern Object: Default hit test width (slightly wider than body for easier clicking) (pixels).
    /// </summary>
    public const float BarPatternHitTestWidth = 10f;

    /// <summary>
    /// Long/Short Position Object: Default visual width of the position box (pixels).
    /// </summary>
    public const float LongShortBoxWidth = 200f;

    /// <summary>
    /// Long/Short Position Object: Horizontal offset for R/R text label (pixels).
    /// </summary>
    public const float LongShortTextOffsetX = 5f;

    /// <summary>
    /// Long/Short Position Object: Vertical offset for R/R text label (pixels).
    /// </summary>
    public const float LongShortTextOffsetY = 4f;

    /// <summary>
    /// Long/Short Position Object: Text size for R/R label (pixels).
    /// </summary>
    public const float LongShortTextSize = 12f;

    /// <summary>
    /// Default proximity distance for simple point-based hit testing (pixels).
    /// Used by GhostFeed, LongShortPosition, HarmonicPattern, etc.
    /// </summary>
    public const float DefaultHitProximity = 20f;

    /// <summary>
    /// Ghost Feed Object: Minimum candle spacing fallback (pixels).
    /// </summary>
    public const float GhostFeedMinSpacing = 4f;

    /// <summary>
    /// Harmonic Pattern Object: Dash interval for dashed lines (pixels).
    /// </summary>
    public const float HarmonicDashInterval = 4f;

    /// <summary>
    /// Harmonic Pattern Object: Vertical offset for point labels (pixels).
    /// </summary>
    public const float HarmonicLabelOffsetY = -5f;

    /// <summary>
    /// Harmonic Pattern Object: Proximity distance for point-based hit testing (pixels).
    /// </summary>
    public const float HarmonicHitProximity = 10f;

    /// <summary>
    /// Target Price Projection Object: Text size for target price label (pixels).
    /// </summary>
    public const float ProjectionTextSize = 11f;

    /// <summary>
    /// Target Price Projection Object: Horizontal offset for text label from projection line start (pixels).
    /// </summary>
    public const float ProjectionTextOffsetX = 10f;

    /// <summary>
    /// Target Price Projection Object: Vertical offset for text label above projection line (pixels).
    /// </summary>
    public const float ProjectionTextOffsetY = -5f;

    /// <summary>
    /// Target Price Projection Object: Dash interval for projected target lines (pixels).
    /// </summary>
    public const float ProjectionDashInterval = 6f;

    /// <summary>
    /// Target Price Projection Object: Padding for target price label background (pixels).
    /// </summary>
    public const float ProjectionLabelPadding = 3f;

    /// <summary>
    /// Target Price Projection Object: Extension length of the projection line beyond P3 (pixels).
    /// </summary>
    public const float ProjectionLineExtension = 200f;
    
    /// <summary>
    /// TrendLine Projection: Default alpha for the projection line (0-255).
    /// </summary>
    public const byte DefaultTrendProjectionAlpha = 160;

    /// <summary>
    /// TrendLine Projection: Default dash interval for the projection line (pixels).
    /// </summary>
    public const float DefaultTrendProjectionDashInterval = 4f;

    #endregion
    
    #region Relative Performance Chart

    /// <summary>
    /// Default format string for relative performance percentage labels.
    /// </summary>
    public const string DefaultRelativePerformanceFormat = "+0.000;-0.000;0.000";

    /// <summary>
    /// Default suffix for relative performance percentage labels.
    /// </summary>
    public const string DefaultRelativePerformanceSuffix = "%";

    /// <summary>
    /// Default format string for Ratio mode labels.
    /// </summary>
    public const string DefaultRatioFormat = "F4";

    /// <summary>
    /// Default format string for Z-Score mode labels.
    /// </summary>
    public const string DefaultZScoreFormat = "+0.00;-0.00;0.00";

    /// <summary>
    /// Prefix for Z-Score mode labels.
    /// </summary>
    public const string ZScorePrefix = "Z:";

    /// <summary>
    /// Suffix for the primary benchmark symbol in the legend and tooltips.
    /// </summary>
    public const string BaseLabelSuffix = " (Base)";

    /// <summary>
    /// Alpha value (0-255) for the primary benchmark symbol in the legend to signify it's a reference.
    /// </summary>
    public const byte BaseAlpha = 180;

    /// <summary>
    /// Default format string for Spread mode labels (signed price difference).
    /// Positive: +#,##0.00, Negative: -#,##0.00, Zero: 0.00
    /// </summary>
    public const string DefaultSpreadFormat = "+#,##0.00;-#,##0.00;0.00";
    
    /// <summary>Statistical boundary beyond which Z-score values are clipped to prevent axis compression.</summary>
    public const decimal ZScoreClippingLimit = 3.5m;

    /// <summary>Maximum iterations allowed for axis rendering to prevent hangs.</summary>
    public const int MaxRenderIterationLimit = 500;

    #endregion

    #region Interaction & UX

    /// <summary>Debounce delay for indicator recalculation pipeline (milliseconds).</summary>
    public const int IndicatorCalculationDebounceDelay = 20;

    /// <summary>Debounce delay for heavy data loading operations (milliseconds).</summary>
    public const int DataLoadDebounceDelay = 150;

    /// <summary>Debounce delay for ML-based DTW pattern search (milliseconds).</summary>
    public const int DtwSearchDebounceDelay = 250;

    /// <summary>Maximum display length for symbol name in tab headers.</summary>
    public const int TabHeaderMaxSymbolLength = 12;

    /// <summary>Standard snap targets for viewport candle count calculation.</summary>
    public static readonly int[] ViewportSnapTargets = { 20, 60, 120, 240, 480, 960, 1920, 3840, 7680, 15360, 30720, 61440, 122880, 200000 };

    #endregion
}
