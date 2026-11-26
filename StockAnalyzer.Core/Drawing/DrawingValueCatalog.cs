using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// The drawing tools whose Data-tab values can be calculated without a chart, in the fixed order of the specification
/// (docs/MCP_SERVER_COMMAND_REFERENCE.md section 6.39, "Tools"). Names equal the <c>ChartObjectType</c> member names.
/// </summary>
public static class DrawingValueCatalog
{
    // The names other layers branch on (the MCP image draws these five and the drawings tool tells the two positions apart): the one definition, used by the table below.
    public const string TrendLineName = "TrendLine";
    public const string RayName = "Ray";
    public const string HorizontalLineName = "HorizontalLine";
    public const string VerticalLineName = "VerticalLine";
    public const string ParallelChannelName = "ParallelChannel";
    public const string LongPositionName = "LongPosition";
    public const string ShortPositionName = "ShortPosition";

    private static readonly DrawingValueDescriptor[] Descriptors =
    {
        new(TrendLineName, 2, r => TrendLineValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color), "TrendLineObject"),
        new(RayName, 2, r => RayValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.CurrentPrice, r.Color), "RayObject"),
        new(HorizontalLineName, 1, r => HorizontalLineValues.Calculate(r.Points[0], r.Color), "HorizontalLineObject"),
        new(VerticalLineName, 1, r => VerticalLineValues.Calculate(r.Points[0], r.Timestamp, r.Color), "VerticalLineObject"),
        new("Arrow", 2, r => ArrowValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color), "ArrowObject"),
        new("PriceLabel", 1, r => PriceLabelValues.Calculate(r.Points[0], r.CurrentPrice, r.Color), "PriceLabelObject"),
        new("FibonacciRetracement", 2, r => FibonacciRetracementValues.Calculate(r.Points[0], r.Points[1], r.CurrentPrice, r.Color, r.Options.ShowExtensions), "FibonacciRetracementObject"),
        new("FibonacciFan", 2, r => FibonacciFanValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color), "FibonacciFanObject"),
        new("FibonacciExpansion", 3, r => FibonacciExpansionValues.Calculate(r.Points[0], r.Points[1], r.Points[2], r.Color), "FibonacciExpansionObject"),
        new("FibonacciChannel", 3, r => FibonacciChannelValues.Calculate(r.Points[0], r.Points[1], r.Points[2], r.Timestamp, r.Color), "FibonacciChannelObject"),
        new(ParallelChannelName, 3, r => ParallelChannelValues.Calculate(r.Points[0], r.Points[1], r.Points[2], r.Timestamp, r.Color), "ParallelChannelObject"),
        new("GannFan", 2, r => GannFanValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color), "GannFanObject"),
        new("GannBox", 2, r => GannBoxValues.Calculate(r.Points[0], r.Points[1], r.Color), "GannBoxObject"),
        new("SineLine", 2, r => SineLineValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color), "SineLineObject"),
        new("TimeCycles", 2, r => TimeCyclesValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color), "TimeCyclesObject"),
        new("Pitchfork", 3, r => PitchforkValues.Calculate(r.Options.PitchforkKind, r.Points[0], r.Points[1], r.Points[2], r.Timestamp, r.Color), "PitchforkObject"),
        new("AngleTool", 2, r => AngleToolValues.Calculate(r.Points[0], r.Points[1], r.Timestamp, r.Color), "AngleObject"),
        new("GannSquareOfNine", 1, r => GannSquareOfNineValues.Calculate(r.Points[0], r.Color), "GannSquareOfNineObject"),
        new("TargetPriceProjection", 3, r => TargetPriceProjectionValues.Calculate(r.Points, r.CurrentPrice, r.Color, r.Options.ProjectionTargets ?? ProjectionTargetSelection.All), "TargetPriceProjectionObject"),
        new(LongPositionName, 3, r => LongShortPositionValues.Calculate(r.Points[0], r.Points[1], r.Points[2], r.Color, r.Color, r.Color, r.Options.IndicatorValueAtEntry), "LongShortPositionObject"),
        new(ShortPositionName, 3, r => LongShortPositionValues.Calculate(r.Points[0], r.Points[1], r.Points[2], r.Color, r.Color, r.Color, r.Options.IndicatorValueAtEntry), "LongShortPositionObject"),
        new("RegressionTrend", 2, r => r.Candles is null
            ? Array.Empty<DrawingCalculatedValue>()
            : RegressionTrendValues.Calculate(RegressionTrendValues.Fit(r.Candles, r.Points[0].Time, r.Points[1].Time), r.Timestamp, r.Options.StdDevMultiplier, r.Options.ExtendRight, r.Color), "RegressionTrendObject", RequiresCandles: true),
        new("AnchoredVWAP", 1, r => r.Candles is null
            ? Array.Empty<DrawingCalculatedValue>()
            : AnchoredVwapValues.Calculate(r.Candles, r.Points[0].Time, r.Timestamp, r.Options.PriceSource, r.Options.ShowBand1, r.Options.Band1Multiplier, r.Options.ShowBand2, r.Options.Band2Multiplier, r.Color), "AnchoredVwapObject", RequiresCandles: true),
        new("ClassicPivotPoints", 1, r => r.Candles is null
            ? Array.Empty<DrawingCalculatedValue>()
            : ClassicPivotPointsValues.Calculate(r.Points[0].Time, r.Candles, r.Options.PivotPeriod, r.Options.PivotExtendMode, r.Color), "ClassicPivotPointsObject", RequiresCandles: true),
        new("CrossLine", 1, r => CrossLineValues.Calculate(r.Points[0], r.Timestamp, r.Color), "CrossLineObject"),
        new("DatePriceRange", 2, r => DatePriceRangeValues.Calculate(r.Points[0], r.Points[1], r.Candles, r.Color), "DatePriceRangeObject", RequiresCandles: true),
        new("DistributionRange", 2, r => DistributionRangeValues.Calculate(r.Points[0], r.Points[1], r.Candles, r.Color, r.Options), "DistributionRangeObject", RequiresCandles: true),
    };

    /// <summary>All tools in the fixed specification order.</summary>
    public static IReadOnlyList<DrawingValueDescriptor> All => Descriptors;

    /// <summary>
    /// Finds the tool of a saved chart object: the object's <c>TypeName</c> (<see cref="DrawingValueDescriptor.StoredTypeName"/>) and, for the one class that serves two tools
    /// (LongShortPositionObject), the tool name the object saved as its <c>type</c> parameter. False when no tool of the catalog has that type name, or when the class serves
    /// several tools and <paramref name="savedToolName"/> names none of them.
    /// </summary>
    public static bool TryGetStored(string? storedTypeName, string? savedToolName, out DrawingValueDescriptor descriptor)
    {
        DrawingValueDescriptor? only = null;
        int matches = 0;
        foreach (DrawingValueDescriptor candidate in Descriptors)
        {
            if (!string.Equals(candidate.StoredTypeName, storedTypeName, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(candidate.Name, savedToolName, StringComparison.Ordinal))
            {
                descriptor = candidate;
                return true;
            }

            only = candidate;
            matches++;
        }

        if (matches == 1)
        {
            descriptor = only!;
            return true;
        }

        descriptor = null!;
        return false;
    }

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
