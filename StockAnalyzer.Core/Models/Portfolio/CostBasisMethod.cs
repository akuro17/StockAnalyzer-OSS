namespace StockAnalyzer.Core.Models.Portfolio;

/// <summary>
/// How the average cost per unit of a position is carried after a partial exit. Realized PnL always uses the FIFO lots; only the average of the
/// quantity that stays open differs. The values are not persisted.
/// </summary>
public enum CostBasisMethod
{
    /// <summary>The average cost stays what it was before the exit (the behaviour before the FIFO method existed).</summary>
    LegacyAveragePreserve = 0,

    /// <summary>The average cost is recalculated from the lots that are still open, so realized plus unrealized PnL equals the economic result.</summary>
    FifoRemainingLots = 1,
}
