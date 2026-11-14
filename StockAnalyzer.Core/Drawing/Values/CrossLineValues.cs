using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing.Values;

/// <summary>
/// Data-tab values of the cross line: the horizontal line's values followed by the vertical line's values, in one
/// fixed-length array. It calls the two existing calculators and defines no formula of its own.
/// </summary>
public static class CrossLineValues
{
    public static IReadOnlyList<DrawingCalculatedValue> Calculate(DrawingAnchor point, DateTime timestamp, IndicatorColor color)
    {
        IReadOnlyList<DrawingCalculatedValue> horizontal = HorizontalLineValues.Calculate(point, color);
        IReadOnlyList<DrawingCalculatedValue> vertical = VerticalLineValues.Calculate(point, timestamp, color);

        var values = new DrawingCalculatedValue[horizontal.Count + vertical.Count];
        for (int i = 0; i < horizontal.Count; i++) values[i] = horizontal[i];
        for (int i = 0; i < vertical.Count; i++) values[horizontal.Count + i] = vertical[i];
        return values;
    }
}
