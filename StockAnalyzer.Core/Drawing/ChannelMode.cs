namespace StockAnalyzer.Core.Drawing;

/// <summary>
/// How the opposite edge of a three-point channel is placed. Single definition shared by the GUI object and the value calculator.
/// Values are persisted by name: never rename or renumber.
/// </summary>
public enum ChannelMode
{
    /// <summary>The opposite edge is parallel to the base edge P1-P2 and passes through P3 (the original behaviour).</summary>
    Parallel = 0,

    /// <summary>The opposite edge is horizontal at the price of P3, between the times of P1 and P2.</summary>
    FlatOpposite = 1
}
