namespace StockAnalyzer.Core.Models.Indicators.PatternFlags;

/// <summary>
/// Which directions of a bidirectional chart-pattern flag indicator may set the flag. The values are explicit and never renumbered or reordered:
/// the enum is persisted with the parameter classes of the flag indicators (SA_ARCHITECTURE_RULES section 7).
/// </summary>
public enum PatternDirectionMode
{
    /// <summary>Both a bullish and a bearish pattern set the flag.</summary>
    [System.ComponentModel.Description("Both")]
    Both = 0,

    /// <summary>Only a bullish pattern sets the flag; a bearish one gives Main = 0m and Direction = 0m.</summary>
    [System.ComponentModel.Description("Bullish Only")]
    BullishOnly = 1,

    /// <summary>Only a bearish pattern sets the flag; a bullish one gives Main = 0m and Direction = 0m.</summary>
    [System.ComponentModel.Description("Bearish Only")]
    BearishOnly = 2,
}

/// <summary>How the value at bar t is defined (plan section 4, S-3).</summary>
public enum PatternFlagEvaluationModel
{
    /// <summary>Window-local (model W): the value at t is a pure function of the bars of the window that ends at t.</summary>
    Window = 0,

    /// <summary>One pass over the whole series (model P): prefix-invariant, used by state machines (BOS/CHoCH) and by Granville.</summary>
    FullSeries = 1,
}

/// <summary>
/// The answer of a detector adapter for one bar: whether the pattern is present and, for a bidirectional type, its direction (-1 bearish, +1 bullish).
/// A type without a direction reports 0.
/// </summary>
public readonly record struct PatternFlagOutcome(bool IsMet, int Direction)
{
    /// <summary>The pattern is not present.</summary>
    public static PatternFlagOutcome None => default;

    /// <summary>The pattern is present and has no direction (the name of the type carries it, or it has none).</summary>
    public static PatternFlagOutcome Met => new(true, 0);

    /// <summary>A bullish pattern is present (+1m).</summary>
    public static PatternFlagOutcome Bullish => new(true, 1);

    /// <summary>A bearish pattern is present (-1m).</summary>
    public static PatternFlagOutcome Bearish => new(true, -1);
}
