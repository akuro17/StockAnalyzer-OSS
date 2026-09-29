namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Configuration class for chart rendering options.
/// This class is designed to be used with IOptions{T} pattern for DI.
/// </summary>
public class ChartOptions
{
    #region Chart Margins

    /// <summary>
    /// Horizontal margin for chart drawing area (pixels).
    /// </summary>
    public float ChartMarginHorizontal { get; set; } = ChartConstants.DefaultChartMarginHorizontal;

    /// <summary>
    /// Top margin for chart drawing area (pixels).
    /// </summary>
    public float ChartMarginTop { get; set; } = ChartConstants.DefaultChartMarginTop;

    /// <summary>
    /// Bottom margin for chart drawing area (pixels).
    /// </summary>
    public float ChartMarginBottom { get; set; } = ChartConstants.DefaultChartMarginBottom;

    #endregion

    #region Crosshair

    /// <summary>
    /// Padding around crosshair labels (pixels).
    /// </summary>
    public float LabelPadding { get; set; } = ChartConstants.DefaultLabelPadding;

    /// <summary>
    /// Height of crosshair labels (pixels).
    /// </summary>
    public float LabelHeight { get; set; } = ChartConstants.DefaultLabelHeight;

    #endregion

    #region ReverseWatch

    /// <summary>
    /// Margin for ReverseWatch renderer (pixels).
    /// </summary>
    public float ReverseWatchMargin { get; set; } = ChartConstants.DefaultReverseWatchMargin;

    /// <summary>
    /// Point radius for data points (pixels).
    /// </summary>
    public float PointRadius { get; set; } = ChartConstants.DefaultPointRadius;

    /// <summary>
    /// Radius for hovered points (pixels).
    /// </summary>
    public float HoverPointRadius { get; set; } = ChartConstants.DefaultHoverPointRadius;

    /// <summary>
    /// Distance threshold for hover detection (pixels).
    /// </summary>
    public float HoverThresholdDistance { get; set; } = ChartConstants.DefaultHoverThresholdDistance;

    #endregion

    #region ReverseWatch Curve Window

    /// <summary>
    /// Margin for ReverseWatch Curve Window (pixels).
    /// </summary>
    public float CurveWindowMargin { get; set; } = ChartConstants.DefaultCurveWindowMargin;

    /// <summary>
    /// Height of time axis (pixels).
    /// </summary>
    public float TimeAxisHeight { get; set; } = ChartConstants.DefaultTimeAxisHeight;

    #endregion
}
