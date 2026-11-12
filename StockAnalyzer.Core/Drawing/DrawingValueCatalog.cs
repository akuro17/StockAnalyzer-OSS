using StockAnalyzer.Core.Drawing.Values;

namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// The drawing tools whose Data-tab values can be calculated without a chart, in the fixed order of the specification
/// (docs/MCP_SERVER_COMMAND_REFERENCE.md section 6.39, "Tools"). Names equal the <c>ChartObjectType</c> member names.
/// </summary>
public static class DrawingValueCatalog
{
    private static readonly DrawingValueDescriptor[] Descriptors =
    {
        new("TrendLine", 2, r => TrendLineValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color)),
        new("Ray", 2, r => RayValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.CurrentPrice, r.Color)),
        new("HorizontalLine", 1, r => HorizontalLineValues.Calculate(r.Points[0], r.Color)),
        new("VerticalLine", 1, r => VerticalLineValues.Calculate(r.Points[0], r.Timestamp, r.Color)),
        new("Arrow", 2, r => ArrowValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color)),
        new("PriceLabel", 1, r => PriceLabelValues.Calculate(r.Points[0], r.CurrentPrice, r.Color)),
        new("FibonacciRetracement", 2, r => FibonacciRetracementValues.Calculate(r.Points[0], r.Points[1], r.CurrentPrice, r.Color, r.Options.ShowExtensions)),
        new("FibonacciFan", 2, r => FibonacciFanValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color)),
        new("FibonacciExpansion", 3, r => FibonacciExpansionValues.Calculate(r.Points[0], r.Points[1], r.Points[2], r.Color)),
        new("FibonacciChannel", 3, r => FibonacciChannelValues.Calculate(r.Points[0], r.Points[1], r.Points[2], r.Timestamp, r.Color)),
        new("ParallelChannel", 3, r => ParallelChannelValues.Calculate(r.Points[0], r.Points[1], r.Points[2], r.Timestamp, r.Color)),
        new("GannFan", 2, r => GannFanValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color)),
        new("GannBox", 2, r => GannBoxValues.Calculate(r.Points[0], r.Points[1], r.Color)),
        new("SineLine", 2, r => SineLineValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color)),
        new("TimeCycles", 2, r => TimeCyclesValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color)),
    };

    /// <summary>All tools in the fixed specification order.</summary>
    public static IReadOnlyList<DrawingValueDescriptor> All => Descriptors;

    /// <summary>Finds a tool by its exact (ordinal, case-sensitive) name; null or an unknown name returns false.</summary>
    public static bool TryGet(string? name, out DrawingValueDescriptor descriptor)
    {
        foreach (DrawingValueDescriptor candidate in Descriptors)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                descriptor = candidate;
                return true;
            }
        }

        descriptor = null!;
        return false;
    }
}
