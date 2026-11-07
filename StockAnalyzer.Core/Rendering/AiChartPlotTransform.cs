using StockAnalyzer.Utils;

namespace StockAnalyzer.Core.Rendering;

/// <summary>
/// Price and bar index to image pixels for one panel. It is the application's <see cref="CoordinateTransform"/> (which works in panel-local coordinates)
/// plus the offset of the panel inside the image, so a price never meets a pixel except through that transformation layer. Pixel values are doubles in
/// pixel-edge coordinates (the origin is the top left corner of the image; the pixel that holds the point (x, y) is (floor x, floor y)); prices stay decimal.
/// </summary>
public sealed class AiChartPlotTransform
{
    private readonly CoordinateTransform _transform = new();
    private readonly decimal _max;
    private readonly decimal _min;
    private readonly int _count;

    public AiChartPlotTransform(int plotLeftPx, int plotTopPx, int plotWidthPx, int plotHeightPx, decimal min, decimal max, int barCount)
    {
        if (plotWidthPx <= 0 || plotHeightPx <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(plotWidthPx), "The plot area must have a positive size.");
        }

        if (max <= min)
        {
            // The transformation layer answers the panel centre for such a range; the caller widens a flat range first (AiChartPriceRange).
            throw new ArgumentException("The value range must have a positive width.", nameof(max));
        }

        if (barCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(barCount), barCount, "At least one bar.");
        }

        PlotLeftPx = plotLeftPx;
        PlotTopPx = plotTopPx;
        PlotWidthPx = plotWidthPx;
        PlotHeightPx = plotHeightPx;
        _min = min;
        _max = max;
        _count = barCount;
    }

    public int PlotLeftPx { get; }

    public int PlotTopPx { get; }

    public int PlotWidthPx { get; }

    public int PlotHeightPx { get; }

    /// <summary>The horizontal room of one bar in pixels.</summary>
    public double BarSpacingPx => (double)PlotWidthPx / _count;

    /// <summary>The y of a value, in image pixels.</summary>
    public double YPx(decimal value) => PlotTopPx + _transform.GetY(value, _max, _min, PlotHeightPx);

    /// <summary>The value at an image y; the inverse of <see cref="YPx"/>.</summary>
    public decimal ValueAt(double yPx) => _transform.GetPrice(yPx - PlotTopPx, _max, _min, PlotHeightPx);

    /// <summary>The x of the centre of a bar, in image pixels.</summary>
    public double CenterXPx(int index) => PlotLeftPx + _transform.GetX(index, _count, PlotWidthPx);

    /// <summary>The width of a candle in the transformation layer (80% of the bar spacing); the renderer turns it into whole pixels.</summary>
    public double RawBodyWidthPx => _transform.GetCandleWidth(_count, PlotWidthPx);

    /// <summary>The reported precision of a pixel position: two decimals. The picture and the reported numbers use the same rounded value.</summary>
    public static double Snap(double pixel) => Math.Round(pixel, 2, MidpointRounding.AwayFromZero);

    /// <summary>The pixel row or column that holds a snapped coordinate.</summary>
    public static int PixelOf(double snapped) => (int)Math.Floor(snapped);
}

/// <summary>The value range of a price panel, with the space around the data that the application's price panel adds.</summary>
public static class AiChartPriceRange
{
    /// <summary>
    /// The high and low of the drawn candles and of the values of the drawn series, then padding on both sides as <c>PricePaddingPx</c> pixels of the panel
    /// (price per pixel = range / panel height, as the application's price panel does). A range of zero uses one price unit per panel height for that
    /// conversion, so the result always has a positive width, also for zero and negative values.
    /// The caller passes only values that are drawn and inside the drawn window (no warm-up values, no series left out of the image): rule R-3 of plan 15.
    /// </summary>
    public static (decimal Min, decimal Max) Compute(IReadOnlyList<CandleData> candles, IEnumerable<IReadOnlyList<decimal?>>? drawnSeries, int plotHeightPx)
    {
        if (candles.Count == 0)
        {
            throw new ArgumentException("At least one candle.", nameof(candles));
        }

        decimal min = decimal.MaxValue;
        decimal max = decimal.MinValue;
        foreach (CandleData candle in candles)
        {
            min = Math.Min(min, candle.Low);
            max = Math.Max(max, candle.High);
        }

        if (drawnSeries is not null)
        {
            foreach (IReadOnlyList<decimal?> series in drawnSeries)
            {
                foreach (decimal? value in series)
                {
                    if (value is { } v)
                    {
                        min = Math.Min(min, v);
                        max = Math.Max(max, v);
                    }
                }
            }
        }

        decimal range = max - min;
        if (range == 0m)
        {
            range = 1m;
        }

        decimal pricePerPixel = range / Math.Max(1, plotHeightPx);
        return (min - (AiChartImageStyle.PricePaddingPx * pricePerPixel), max + (AiChartImageStyle.PricePaddingPx * pricePerPixel));
    }

    /// <summary>
    /// The range of an oscillator panel, with the rule and numbers of the application's sub-panel range (<c>PanelValueRangeCalculator</c>): the indicator's fixed range when it has one;
    /// otherwise the smallest and largest drawn value (zero included when <paramref name="includeZeroBaseline"/>), widened by 5% of the range on both sides, or by 10% of the value
    /// (0.05 for zero) when all values are the same; 0 to 1 when there is no value (the application's range for a successful result without a value). The values are the drawn ones
    /// inside the drawn window only (rule R-3 of plan 15). A fixed range whose maximum is not above its minimum is ignored and the range comes from the values (owner decision E-2 = D2-A):
    /// the application would return it as it is, but the transformation layer needs a positive width, and no tool call can produce such a range (the registry defaults all have minimum
    /// below maximum and MCP sets none). Whatever the input, the result has a positive width.
    /// </summary>
    public static (decimal Min, decimal Max) ComputeOscillator(IEnumerable<IReadOnlyList<decimal?>> drawnSeries, decimal? fixedMin, decimal? fixedMax, bool includeZeroBaseline)
    {
        if (fixedMin is { } fixedLow && fixedMax is { } fixedHigh && fixedHigh > fixedLow)
        {
            return (fixedLow, fixedHigh);
        }

        decimal min = decimal.MaxValue;
        decimal max = decimal.MinValue;
        bool hasValue = false;
        foreach (IReadOnlyList<decimal?> series in drawnSeries)
        {
            foreach (decimal? value in series)
            {
                if (value is { } v)
                {
                    min = Math.Min(min, v);
                    max = Math.Max(max, v);
                    hasValue = true;
                }
            }
        }

        if (!hasValue)
        {
            return (AiChartImageStyle.OscillatorEmptyMin, AiChartImageStyle.OscillatorEmptyMax);
        }

        if (includeZeroBaseline)
        {
            min = Math.Min(min, 0m);
            max = Math.Max(max, 0m);
        }

        decimal buffer = (max - min) * AiChartImageStyle.OscillatorBufferRatio;
        if (buffer == 0m)
        {
            buffer = Math.Abs(min) > 0m ? Math.Abs(min) * AiChartImageStyle.OscillatorFlatBufferRatio : AiChartImageStyle.OscillatorFlatBufferOfZero;
        }

        return (min - buffer, max + buffer);
    }

    /// <summary>The range of the volume panel: from zero to the largest volume; one when every volume is zero.</summary>
    public static (decimal Min, decimal Max) ComputeVolume(IReadOnlyList<CandleData> candles)
    {
        long largest = 0;
        foreach (CandleData candle in candles)
        {
            largest = Math.Max(largest, candle.Volume);
        }

        return (0m, largest == 0 ? 1m : largest);
    }
}
