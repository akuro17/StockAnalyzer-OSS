namespace StockAnalyzer.Core.Models.Portfolio;

/// <summary>
/// The one definition of the accounting convention the application uses when it rebuilds a portfolio for display: the portfolio screen and the
/// read-only MCP source both read it, so the same history gives the same positions in both.
/// </summary>
public static class PortfolioAccountingDefaults
{
    /// <summary>Owner decision D-1=A (2026-11-26): the average cost of the quantity still open is the average of the remaining FIFO lots.</summary>
    public const CostBasisMethod CostBasis = CostBasisMethod.FifoRemainingLots;
}
