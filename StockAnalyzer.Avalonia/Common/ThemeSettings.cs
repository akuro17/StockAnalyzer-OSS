namespace StockAnalyzer.Avalonia.Common;

/// <summary>
/// Configuration class for chart theme settings (colors).
/// This class is designed to be used with IOptions{T} pattern for DI.
/// Hex color strings are used for JSON serialization compatibility.
/// </summary>
public class ThemeSettings
{
    #region Chart Background

    /// <summary>
    /// Background color for the main chart area (hex format, e.g., "#FAEABB").
    /// </summary>
    public string ChartBackground { get; set; } = "#FAEABB";

    #endregion

    #region Grid & Axes

    /// <summary>
    /// Grid line color (hex format).
    /// </summary>
    public string GridLineColor { get; set; } = "#D3D3D3";

    /// <summary>
    /// Axis text color (hex format).
    /// </summary>
    public string AxisTextColor { get; set; } = "#16161A";

    /// <summary>
    /// Crosshair line color (hex format).
    /// </summary>
    public string CrosshairColor { get; set; } = "#808080";

    #endregion

    #region Candle Colors

    /// <summary>
    /// Bullish (up) candle color - Japanese style: Red (hex format).
    /// </summary>
    public string BullishColor { get; set; } = "#FF0000";

    /// <summary>
    /// Bearish (down) candle color - Japanese style: Green (hex format).
    /// </summary>
    public string BearishColor { get; set; } = "#008000";

    /// <summary>
    /// Neutral candle color (hex format).
    /// </summary>
    public string NeutralColor { get; set; } = "#808080";

    #endregion

    #region Semantic Colors

    /// <summary>
    /// Plus value color (hex format).
    /// </summary>
    public string SemanticPlusColor { get; set; } = "#FF388E3C";

    /// <summary>
    /// Minus value color (hex format).
    /// </summary>
    public string SemanticMinusColor { get; set; } = "#FFC62828";

    /// <summary>
    /// Neutral value color (hex format).
    /// </summary>
    public string SemanticNeutralColor { get; set; } = "#FF787B86";

    #endregion

    #region Volume Colors

    /// <summary>
    /// Volume bar color for up candles (hex format with alpha).
    /// </summary>
    public string VolumeUpColor { get; set; } = "#80FF0000";

    /// <summary>
    /// Volume bar color for down candles (hex format with alpha).
    /// </summary>
    public string VolumeDownColor { get; set; } = "#80008000";

    #endregion

    #region Tools

    /// <summary>
    /// Ruler fill color (hex format with alpha).
    /// </summary>
    public string RulerFillColor { get; set; } = "#1E0000FF";

    /// <summary>
    /// Ruler stroke color (hex format).
    /// </summary>
    public string RulerStrokeColor { get; set; } = "#0000FF";

    #endregion
}
