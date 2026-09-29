namespace StockAnalyzer.Core.Models.GeometricPattern;

/// <summary>
/// Enumerates the geometric chart formation types detectable by the engine.
/// These are macro-level patterns formed by the convergence/divergence of trendlines.
/// </summary>
public enum GeometricFormationType
{
    /// <summary>Two parallel lines sloping upward (uptrend continuation).</summary>
    AscendingChannel,

    /// <summary>Two parallel lines sloping downward (downtrend continuation).</summary>
    DescendingChannel,

    /// <summary>Small parallel counter-trend channel preceded by a strong pole (continuation).</summary>
    BullishFlag,

    /// <summary>Small parallel counter-trend channel preceded by a strong pole (continuation).</summary>
    BearishFlag,

    /// <summary>Converging lines preceded by a strong pole (continuation).</summary>
    Pennant,

    /// <summary>Both lines converge symmetrically toward an apex.</summary>
    SymmetricalTriangle,

    /// <summary>Sideways consolidation with flat support and resistance.</summary>
    HorizontalBox,

    /// <summary>Both lines slope upward but converge (resistance rises slower than support).</summary>
    RisingWedge,

    /// <summary>Both lines slope downward but converge (support falls slower than resistance).</summary>
    FallingWedge,

    /// <summary>Flat resistance line with rising support line.</summary>
    AscendingTriangle,

    /// <summary>Flat support line with declining resistance line.</summary>
    DescendingTriangle,

    /// <summary>Both lines diverge (expanding formation).</summary>
    Megaphone
}
