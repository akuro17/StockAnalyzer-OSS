namespace StockAnalyzer.Core.Models.Backtest
{
    /// <summary>
    /// Calculation convention of the per-trade statistics in <see cref="BacktestStatistics"/>. A stored result names the version that
    /// produced it, so numbers computed under different conventions are never compared unknowingly.
    /// </summary>
    public enum TradeStatisticsVersion
    {
        /// <summary>Legacy: the standard deviations behind Sharpe/Sortino use the population divisor N (NumPy ddof=0).</summary>
        V1PopulationStdDev = 1,

        /// <summary>The standard deviations use the sample divisor N-1 (ddof=1), as the bar-level report metrics already do.</summary>
        V2SampleStdDev = 2,
    }
}
