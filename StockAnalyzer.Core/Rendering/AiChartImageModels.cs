namespace StockAnalyzer.Core.Rendering;

/// <summary>One entry of the legend: a line sample and its label. The label is drawn as ASCII text (other characters become '?').</summary>
public sealed record AiChartLegendEntry(string Label, string ColorHex, AiChartLineStyle LineStyle, float LineWidthPx);

/// <summary>A line of an indicator: its name in the indicator result and its values for the drawn window (exactly one per drawn bar, null where there is no value).</summary>
public sealed record AiChartLineSeries(string Name, IReadOnlyList<decimal?> Values);

/// <summary>
/// An indicator drawn on the price panel. <paramref name="RepresentativePeriod"/> ranks a moving average (null for a type without a period, or a type that is not one);
/// <paramref name="Series"/> come from <see cref="AiChartSeriesSelector"/>.
/// </summary>
public sealed record AiChartOverlay(string Id, IndicatorType Type, int? RepresentativePeriod, IReadOnlyList<AiChartLineSeries> Series);

/// <summary>
/// An indicator drawn in a panel of its own. <paramref name="FixedMin"/> and <paramref name="FixedMax"/> are the indicator's fixed value range from its settings (both or none);
/// <paramref name="IncludeZeroBaseline"/> draws a zero line and keeps zero inside the range.
/// </summary>
public sealed record AiChartOscillator(string Id, IReadOnlyList<AiChartLineSeries> Series, decimal? FixedMin, decimal? FixedMax, bool IncludeZeroBaseline);

/// <summary>A buy or sell marker on a drawn bar (index 0 is the oldest drawn bar).</summary>
public sealed record AiChartMarker(int BarIndex, bool IsBuy);

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
    IReadOnlyList<AiChartMarker>? Markers = null);

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
    int LegendEntriesDrawn);
