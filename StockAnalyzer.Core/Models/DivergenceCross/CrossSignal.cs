namespace StockAnalyzer.Core.Models.DivergenceCross;

/// <summary>
/// Represents a detected cross event between two series (e.g., Golden Cross, Dead Cross).
/// </summary>
public sealed class CrossSignal
{
    /// <summary>The type of cross detected (GoldenCross or DeadCross).</summary>
    public SignalType Type { get; }

    /// <summary>The index in the data array where the cross occurs.</summary>
    public int CrossIndex { get; }

    /// <summary>The value of the short-period series at the cross point.</summary>
    public decimal ShortValue { get; }

    /// <summary>The value of the long-period series at the cross point.</summary>
    public decimal LongValue { get; }

    public CrossSignal(SignalType type, int crossIndex, decimal shortValue, decimal longValue)
    {
        Type = type;
        CrossIndex = crossIndex;
        ShortValue = shortValue;
        LongValue = longValue;
    }

    public override string ToString()
        => $"{Type} at [{CrossIndex}] Short={ShortValue} Long={LongValue}";
}
