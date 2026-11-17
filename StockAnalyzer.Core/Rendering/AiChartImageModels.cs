using StockAnalyzer.Core.Drawing;

namespace StockAnalyzer.Core.Rendering;

/// <summary>How the values of a series are drawn: a line through the bars, or one dot per bar (Parabolic SAR).</summary>
public enum AiChartLineShape
{
    Line,
    Dots,
}

/// <summary>
/// One entry of the legend: a line sample and its label. The label is drawn as ASCII text (other characters become '?'). For a series drawn as <see cref="AiChartLineShape.Dots"/>
/// the sample is a row of dots and the line style and width are not used by the picture.
/// </summary>
public sealed record AiChartLegendEntry(string Label, string ColorHex, AiChartLineStyle LineStyle, float LineWidthPx, AiChartLineShape Shape = AiChartLineShape.Line);

/// <summary>A line of an indicator: its name in the indicator result and its values for the drawn window (exactly one per drawn bar, null where there is no value).</summary>
public sealed record AiChartLineSeries(string Name, IReadOnlyList<decimal?> Values);

/// <summary>The two boundaries of a filled cloud (the Kumo of Ichimoku): exactly one value per drawn bar each, null where there is none. The cloud is drawn only between bars that have both.</summary>
public sealed record AiChartCloud(IReadOnlyList<decimal?> SpanA, IReadOnlyList<decimal?> SpanB);

/// <summary>
/// An indicator drawn on the price panel. <paramref name="RepresentativePeriod"/> ranks a moving average (null for a type without a period, or a type that is not one);
/// <paramref name="Series"/> come from <see cref="AiChartSeriesSelector"/>. <paramref name="Shape"/> says how every series of the overlay is drawn;
/// <paramref name="Cloud"/> adds a filled cloud under the lines (null for none).
/// </summary>
public sealed record AiChartOverlay(
    string Id,
    IndicatorType Type,
    int? RepresentativePeriod,
    IReadOnlyList<AiChartLineSeries> Series,
    AiChartLineShape Shape = AiChartLineShape.Line,
    AiChartCloud? Cloud = null);

/// <summary>
/// An indicator drawn in a panel of its own. <paramref name="FixedMin"/> and <paramref name="FixedMax"/> are the indicator's fixed value range from its settings (both or none);
/// <paramref name="IncludeZeroBaseline"/> draws a zero line and keeps zero inside the range. <paramref name="HistogramValues"/> are the values of the histogram series
/// (one per drawn bar, null where there is none), drawn as bars from zero behind the lines; null for no histogram. A histogram keeps its values and zero inside the range.
/// </summary>
public sealed record AiChartOscillator(
    string Id,
    IReadOnlyList<AiChartLineSeries> Series,
    decimal? FixedMin,
    decimal? FixedMax,
    bool IncludeZeroBaseline,
    IReadOnlyList<decimal?>? HistogramValues = null);

/// <summary>A buy or sell marker on a drawn bar (index 0 is the oldest drawn bar).</summary>
public sealed record AiChartMarker(int BarIndex, bool IsBuy);

/// <summary>The kinds of saved drawing an image can draw (all on the price panel).</summary>
public enum AiChartDrawingKind
{
    /// <summary>One point: the line across the panel at its price.</summary>
    HorizontalLine,

    /// <summary>One point: the line across the panel at its bar position.</summary>
    VerticalLine,

    /// <summary>Two points: the segment, prolonged as <see cref="AiChartDrawing.Extend"/> says.</summary>
    TrendLine,

    /// <summary>Two points: starts at the first and runs through the second to the border of the panel.</summary>
    Ray,

    /// <summary>Three points: the base edge from the first to the second point and the opposite edge through the third (see <see cref="AiChartDrawing.ChannelMode"/>).</summary>
    ParallelChannel,
}

/// <summary>A control point of a saved drawing: <paramref name="BarIndex"/> is a position in drawn-bar units (0 is the first drawn bar, 1.5 is half way between the second and the third; it can lie outside the drawn bars).</summary>
public sealed record AiChartDrawingPoint(double BarIndex, decimal Price);

/// <summary>A saved drawing to draw in the fixed colour of the style (the user's colour and line style are not used). The number of points is fixed by the kind.</summary>
public sealed record AiChartDrawing(
    AiChartDrawingKind Kind,
    IReadOnlyList<AiChartDrawingPoint> Points,
    LineExtendMode Extend = LineExtendMode.None,
    ChannelMode ChannelMode = ChannelMode.Parallel);

/// <summary>
/// What to draw. The size must have passed <see cref="AiChartLayout.CheckSize"/> for the number of bars, the volume panel and the oscillator panels. The values of the
/// overlays and oscillators are already cut to the drawn window (see <see cref="AiChartSeriesSelector"/>).
/// </summary>
public sealed record AiChartImageRequest(
    IReadOnlyList<CandleData> Candles,
    int WidthPx,
    int HeightPx,
    AiChartBackground Background,
    bool IncludeVolume,
    IReadOnlyList<AiChartOverlay>? Overlays = null,
    IReadOnlyList<AiChartOscillator>? Oscillators = null,
    IReadOnlyList<AiChartMarker>? Markers = null,
    IReadOnlyList<AiChartDrawing>? Drawings = null);

/// <summary>
/// The mapping of the price panel. Pixels are image pixels in pixel-edge coordinates (origin top left), rounded to two decimals; prices are in the
/// instrument's price unit.
/// </summary>
public sealed record AiChartAxes(
    decimal PriceMin,
    decimal PriceMax,
    double PricePlotLeftPx,
    double PricePlotRightPx,
    double PricePlotTopPx,
    double PricePlotBottomPx,
    double BarSpacingPx,
    double FirstBarCenterXPx);

/// <summary>
/// One panel with its pixel rows and value range, so that a reader can turn a pixel row of any panel back into a value. <paramref name="Id"/> is "price", "volume"
/// or the id of the oscillator. The unit of the values is the caller's to state (price unit, shares, the indicator's own unit).
/// </summary>
public sealed record AiChartPanelInfo(string Id, AiChartPanelKind Kind, double TopPx, double BottomPx, decimal ValueMin, decimal ValueMax);

/// <summary>The pixel position of one drawn bar. Index 0 is the oldest drawn bar.</summary>
public sealed record AiChartBarPosition(int Index, DateTime Date, double XPx, double OpenYPx, double HighYPx, double LowYPx, double CloseYPx);

/// <summary>A pixel position in image pixels (pixel-edge coordinates), rounded to two decimals; it can lie outside the image.</summary>
public sealed record AiChartPixelPoint(double XPx, double YPx);

/// <summary>
/// Where a drawing of the request went: its control points in pixels and whether any part of it lies inside the price panel (a drawing outside the panel leaves no pixels).
/// <paramref name="Index"/> is the position in <see cref="AiChartImageRequest.Drawings"/>.
/// </summary>
public sealed record AiChartDrawingPlacement(int Index, IReadOnlyList<AiChartPixelPoint> Points, bool IsVisible);

/// <summary>
/// The PNG and the mapping that lets a reader turn pixels back into values. <paramref name="Legend"/> lists every line (price panel first, then each oscillator);
/// the picture shows the first <see cref="AiChartImageStyle.MaxLegendEntries"/> of them (<paramref name="LegendEntriesDrawn"/>).
/// </summary>
public sealed record AiChartImageResult(
    byte[] Png,
    int WidthPx,
    int HeightPx,
    AiChartAxes Axes,
    IReadOnlyList<AiChartPanelInfo> Panels,
    IReadOnlyList<AiChartBarPosition> Bars,
    IReadOnlyList<AiChartLegendEntry> Legend,
    int LegendEntriesDrawn,
    IReadOnlyList<AiChartDrawingPlacement>? Drawings = null);
