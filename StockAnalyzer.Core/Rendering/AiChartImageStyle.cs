using SkiaSharp;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Rendering;

/// <summary>Background of an AI chart image: the two backgrounds of the style, no gradient and no pattern.</summary>
public enum AiChartBackground
{
    White,
    LightGray,
}

/// <summary>Line pattern of a drawn series; the n-th line of a panel uses <c>n mod 3</c> so that a series is never told apart by colour alone.</summary>
public enum AiChartLineStyle
{
    Solid,
    Dash,
    Dot,
}

/// <summary>
/// The fixed style "AiRecognitionV1" of the images that sa_render_chart_image returns: the one place that defines every colour, width, margin and
/// layout constant of those images. It does not read the user's theme or settings (the same input always gives the same image) and it is never
/// changed in place: a different look is a new style name. The values come from the colour design document and the owner decisions of plan 15;
/// they are design examples, not values proven to raise recognition accuracy.
/// </summary>
public static class AiChartImageStyle
{
    public const string Name = "AiRecognitionV1";

    // ---- Colours (hex, with the contrast on #FFFFFF / #F5F5F5 where it matters) ----
    public const string BackgroundWhiteHex = "#FFFFFF";
    public const string BackgroundLightGrayHex = "#F5F5F5";
    public const string UpHex = "#D32F2F";
    public const string DownHex = "#1565C0";

    /// <summary>A bar with Open == Close: the application's own neutral candle colour (one definition, owner decision D-15d).</summary>
    public const string DojiHex = ChartSettingsConstants.DefaultNeutralColor;

    public const string MovingAverageShortHex = "#E65100";
    public const string MovingAverageMidHex = "#6A1B9A";
    public const string MovingAverageLongHex = "#00838F";
    public const string SignalHex = "#111111";
    public const string GridHex = "#D9DDE3";
    public const string TextHex = "#212121";
    public const string BorderHex = "#9E9E9E";
    public const string VolumeHex = "#546E7A";

    /// <summary>
    /// Colours of the lines after the three moving-average colours, in order. Taken from the application's fixed light palette
    /// (<c>ChartColorPalette.LightColors</c>: Green, Amber, Pink, Mint, Violet, Coral) after a contrast and colour-difference check (step log of plan 15, T0).
    /// </summary>
    public static readonly IReadOnlyList<string> AuxiliaryHex = Array.AsReadOnly(new[] { "#3B6D11", "#854F0B", "#993556", "#1A6B52", "#534AB7", "#993C1D" });

    // ---- Lines and shapes (pixels) ----
    public const float MovingAverageWidthPx = 2.5f;
    public const float AuxiliaryWidthPx = 1.5f;
    public const int WickWidthPx = 1;

    /// <summary>The narrowest candle body: odd, so that it is centred on the 1 px wick, and wider than the wick so that body and wick never merge.</summary>
    public const int MinBodyWidthPx = 3;

    public const int GridWidthPx = 1;
    public const int BorderWidthPx = 1;
    public const float MarkerSidePx = 9f;

    /// <summary>The line patterns in the order they are given out: the n-th line of a panel takes <c>LinePatterns[n mod LinePatterns.Count]</c>.</summary>
    public static readonly IReadOnlyList<AiChartLineStyle> LinePatterns = Array.AsReadOnly(new[] { AiChartLineStyle.Solid, AiChartLineStyle.Dash, AiChartLineStyle.Dot });

    public static readonly IReadOnlyList<float> DashIntervalsPx = Array.AsReadOnly(new[] { 6f, 3f });
    public static readonly IReadOnlyList<float> DotIntervalsPx = Array.AsReadOnly(new[] { 2f, 3f });

    /// <summary>The narrowest horizontal room of one bar: body (3 px) plus a gap of at least 1 px.</summary>
    public const double MinBarSpacingPx = 4.0;

    // ---- Text: English letters and digits only, no smaller than 11 px ----
    public const float FontSizePx = 11f;
    public const string DateFormat = "yyyy-MM-dd";

    // ---- Fixed margins and panel layout; never derived from a text measurement ----
    public const int MarginLeftPx = 12;

    /// <summary>The right margin holds the value-axis labels.</summary>
    public const int MarginRightPx = 72;

    public const int MarginTopPx = 20;
    public const int MarginBottomPx = 24;
    public const int PanelGapPx = 6;
    public const int MinImageWidthPx = 320;
    public const int MinImageHeightPx = 240;

    /// <summary>Panel heights are in the ratio of these weights (price 6 : volume 1.5 : each oscillator 2).</summary>
    public const double PricePanelWeight = 6.0;

    public const double VolumePanelWeight = 1.5;
    public const double OscillatorPanelWeight = 2.0;
    public const int MaxOscillatorPanels = 2;

    /// <summary>Most lines one indicator contributes to the image (owner decision D-15e=B).</summary>
    public const int MaxSeriesPerIndicator = 3;
    public const int MinPricePanelHeightPx = 120;
    public const int MinSubPanelHeightPx = 32;

    /// <summary>Space above and below the price range, as the application's price panel does (<c>StandardPricePaddingPx</c>).</summary>
    public const int PricePaddingPx = 10;

    // ---- Value range of an oscillator panel (the numbers of the application's sub-panel range, owner decision D-15m-2=A) ----
    /// <summary>
    /// The range of an oscillator panel whose indicator has no value in the drawn window and no usable fixed range: the application's range for a successful result without a value
    /// (owner decision E-2 = D1-B). The image tool never draws such an indicator, so this is a defence of the Core renderer only.
    /// </summary>
    public const decimal OscillatorEmptyMin = 0m;

    public const decimal OscillatorEmptyMax = 1m;

    /// <summary>Space above and below the values, as a share of the value range.</summary>
    public const decimal OscillatorBufferRatio = 0.05m;

    /// <summary>When every value is the same: this share of its size is the space on each side ...</summary>
    public const decimal OscillatorFlatBufferRatio = 0.1m;

    /// <summary>... and when that value is zero, this much.</summary>
    public const decimal OscillatorFlatBufferOfZero = 0.05m;

    // ---- Axis labels ----
    /// <summary>Price-axis labels at these fractions of the panel height from the bottom; none at the edges, so labels of neighbouring panels never touch.</summary>
    public static readonly IReadOnlyList<decimal> PriceTickFractions = Array.AsReadOnly(new[] { 0.2m, 0.4m, 0.6m, 0.8m });

    public static readonly IReadOnlyList<decimal> VolumeTickFractions = Array.AsReadOnly(new[] { 0.5m, 1.0m });
    public const int MaxDateLabels = 6;
    public const int DateLabelMinSpacingPx = 90;
    public const int AxisLabelOffsetPx = 6;

    /// <summary>Labels of an oscillator panel at these fractions of its height from the bottom (two, so that the labels of a 32 px panel do not touch).</summary>
    public static readonly IReadOnlyList<decimal> OscillatorTickFractions = Array.AsReadOnly(new[] { 0.25m, 0.75m });

    /// <summary>Space between a bar and its buy/sell marker.</summary>
    public const int MarkerGapPx = 3;

    // ---- Legend (top right inside the price panel) ----
    public const int MaxLegendEntries = 8;
    public const int LegendRowHeightPx = 16;
    public const int LegendSwatchWidthPx = 24;
    public const int LegendPaddingPx = 6;

    /// <summary>The fill of the legend box: white with this alpha (230 of 255), on a white and on a light grey background alike.</summary>
    public const string LegendFillHex = BackgroundWhiteHex;

    public const byte LegendFillAlpha = 230;

    // ---- Drawing details ----
    /// <summary>The centre of a pixel: a line or a marker is drawn at the pixel column and row of its point plus this much, so it covers whole pixels symmetrically.</summary>
    public const float PixelCentreOffsetPx = 0.5f;

    /// <summary>The height of an equilateral marker triangle as a share of its side: sqrt(3) / 2.</summary>
    public const float MarkerHeightRatio = 0.8660254f;

    /// <summary>The share of the font size by which the text baseline lies below the vertical centre of a row of text.</summary>
    public const float TextCentreRatio = 0.35f;

    /// <summary>The gap between the bottom of the last panel and the date labels, added to the font size.</summary>
    public const float DateLabelOffsetPx = 3f;

    /// <summary>The nearest a date label may come to the left or right edge of the image.</summary>
    public const float DateLabelEdgePx = 2f;

    /// <summary>The quality given to the PNG encoder (lossless: the value has no effect on the pixels).</summary>
    public const int PngQuality = 100;

    // ---- Number formats of the axis labels ----
    /// <summary>A price of this size or more (in absolute value) is written with up to two decimals, a smaller one with up to four.</summary>
    public const decimal PriceFewDecimalsFrom = 1m;

    public const string PriceFormatFewDecimals = "0.##";
    public const string PriceFormatManyDecimals = "0.####";

    /// <summary>The units of a volume label, largest first: a volume of one unit or more is divided by it and gets the suffix.</summary>
    public static readonly IReadOnlyList<(decimal Unit, string Suffix)> VolumeUnits = Array.AsReadOnly(new[] { (1_000_000_000m, "B"), (1_000_000m, "M"), (1_000m, "K") });

    public const string VolumeScaledFormat = "0.##";
    public const string VolumePlainFormat = "0";

    public static SKColor Color(string hex) => SKColor.Parse(hex);

    public static string BackgroundHex(AiChartBackground background) => background == AiChartBackground.LightGray ? BackgroundLightGrayHex : BackgroundWhiteHex;
}
