using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Services.Backtest.Reporting;

/// <summary>
/// Composite performance and equity results produced by <see cref="PortfolioEquityComposer.Compose"/>.
/// Represents the aggregate results of multi-symbol backtesting under the Independent Sleeves model (spec §6.2).
/// </summary>
public sealed record CompositePortfolioResult(
    decimal AllocatedTotal,
    decimal FinalEquity,
    decimal TotalReturn,
    decimal MaxDrawdownRatio,
    RankedDrawdownEpisode? WorstEpisode,
    int PooledTradeCount,
    decimal? PooledWinRate,
    ImmutableArray<EquityPoint> CompositeEquityPoints,
    string? DrawdownUnavailableReason = null);

/// <summary>
/// Input for a single symbol sleeve passed to <see cref="PortfolioEquityComposer.Compose"/>.
/// </summary>
/// <param name="Symbol">Ticker symbol.</param>
/// <param name="SleeveCapital">Allocated sleeve capital (initialCapital / N).</param>
/// <param name="IsSuccess">Whether the sleeve backtest succeeded.</param>
/// <param name="FailureReason">Reason code if backtest failed or has insufficient data.</param>
/// <param name="EquityPoints">Evaluated equity curve points of the sleeve.</param>
/// <param name="Trades">Closed trades from the sleeve run.</param>
public readonly record struct SleeveBacktestInput(
    string Symbol,
    decimal SleeveCapital,
    bool IsSuccess,
    string? FailureReason,
    ImmutableArray<EquityPoint> EquityPoints,
    ImmutableArray<BacktestTrade> Trades);

/// <summary>
/// Pure mathematical composer for multi-symbol basket backtests under the Independent Sleeves model (spec §6.2).
/// Aligns sleeve equity series along their calendar union using forward-fill, sums sleeve equity curves,
/// calculates composite drawdown using <see cref="DrawdownEpisodeEnumerator"/>, and aggregates pooled trade statistics.
/// </summary>
public static class PortfolioEquityComposer
{
    /// <summary>
    /// Composes multiple sleeve backtest results into an aggregate portfolio result.
    /// Pure function: deterministic, no I/O, preserves decimal precision.
    /// </summary>
    /// <param name="initialCapital">Total portfolio initial capital (must be positive).</param>
    /// <param name="sleeves">Sleeve backtest inputs.</param>
    /// <param name="initialTimestamp">Optional initial timestamp for drawdown episode evaluation.</param>
    public static CompositePortfolioResult Compose(
        decimal initialCapital,
        IReadOnlyList<SleeveBacktestInput> sleeves,
        DateTime? initialTimestamp = null)
    {
        if (initialCapital <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(initialCapital), initialCapital, "Initial capital must be positive.");
        }
        ArgumentNullException.ThrowIfNull(sleeves);

        decimal allocatedTotal = 0m;
        foreach (SleeveBacktestInput sleeve in sleeves)
        {
            allocatedTotal += sleeve.SleeveCapital;
        }

        if (sleeves.Count == 0)
        {
            return new CompositePortfolioResult(
                AllocatedTotal: 0m,
                FinalEquity: initialCapital,
                TotalReturn: 0m,
                MaxDrawdownRatio: 0m,
                WorstEpisode: null,
                PooledTradeCount: 0,
                PooledWinRate: null,
                CompositeEquityPoints: ImmutableArray<EquityPoint>.Empty);
        }

        // 1. Gather all unique timestamps from successful sleeves in chronological order
        var dateSet = new SortedSet<DateTime>();
        foreach (SleeveBacktestInput sleeve in sleeves)
        {
            if (sleeve.IsSuccess && !sleeve.EquityPoints.IsDefaultOrEmpty)
            {
                foreach (EquityPoint pt in sleeve.EquityPoints)
                {
                    dateSet.Add(pt.Timestamp);
                }
            }
        }

        // Pooled trade statistics across all successful sleeves
        int pooledTradeCount = 0;
        int winCount = 0;
        foreach (SleeveBacktestInput sleeve in sleeves)
        {
            if (sleeve.IsSuccess && !sleeve.Trades.IsDefaultOrEmpty)
            {
                foreach (BacktestTrade trade in sleeve.Trades)
                {
                    pooledTradeCount++;
                    if (trade.ClosedNet > 0m)
                    {
                        winCount++;
                    }
                }
            }
        }
        decimal? pooledWinRate = pooledTradeCount > 0 ? (decimal)winCount / pooledTradeCount : null;

        // If no trading dates exist (e.g. all sleeves failed or had 0 points)
        if (dateSet.Count == 0)
        {
            return new CompositePortfolioResult(
                AllocatedTotal: allocatedTotal,
                FinalEquity: allocatedTotal,
                TotalReturn: 0m,
                MaxDrawdownRatio: 0m,
                WorstEpisode: null,
                PooledTradeCount: pooledTradeCount,
                PooledWinRate: pooledWinRate,
                CompositeEquityPoints: ImmutableArray<EquityPoint>.Empty);
        }

        DateTime[] dates = new DateTime[dateSet.Count];
        dateSet.CopyTo(dates);

        // 2. Prepare two-pointer indices for each sleeve
        int sleeveCount = sleeves.Count;
        int[] sleevePointers = new int[sleeveCount];
        decimal[] lastEquity = new decimal[sleeveCount];
        decimal[] lastCash = new decimal[sleeveCount];
        decimal[] lastMarketValue = new decimal[sleeveCount];
        decimal[] lastHeldMargin = new decimal[sleeveCount];

        for (int s = 0; s < sleeveCount; s++)
        {
            lastEquity[s] = sleeves[s].SleeveCapital;
            lastCash[s] = sleeves[s].SleeveCapital;
            lastMarketValue[s] = 0m;
            lastHeldMargin[s] = 0m;
        }

        ImmutableArray<EquityPoint>.Builder compositeBuilder = ImmutableArray.CreateBuilder<EquityPoint>(dates.Length);

        for (int k = 0; k < dates.Length; k++)
        {
            DateTime currentUtc = dates[k];
            decimal totalEq = 0m;
            decimal totalCash = 0m;
            decimal totalMarketVal = 0m;
            decimal totalHeldMargin = 0m;

            for (int s = 0; s < sleeveCount; s++)
            {
                SleeveBacktestInput sleeve = sleeves[s];
                if (sleeve.IsSuccess && !sleeve.EquityPoints.IsDefaultOrEmpty)
                {
                    int ptr = sleevePointers[s];
                    while (ptr < sleeve.EquityPoints.Length && sleeve.EquityPoints[ptr].Timestamp < currentUtc)
                    {
                        lastEquity[s] = sleeve.EquityPoints[ptr].Equity;
                        lastCash[s] = sleeve.EquityPoints[ptr].Cash;
                        lastMarketValue[s] = sleeve.EquityPoints[ptr].MarketValue;
                        lastHeldMargin[s] = sleeve.EquityPoints[ptr].HeldMargin;
                        ptr++;
                    }

                    if (ptr < sleeve.EquityPoints.Length && sleeve.EquityPoints[ptr].Timestamp == currentUtc)
                    {
                        lastEquity[s] = sleeve.EquityPoints[ptr].Equity;
                        lastCash[s] = sleeve.EquityPoints[ptr].Cash;
                        lastMarketValue[s] = sleeve.EquityPoints[ptr].MarketValue;
                        lastHeldMargin[s] = sleeve.EquityPoints[ptr].HeldMargin;
                        ptr++;
                    }

                    sleevePointers[s] = ptr;
                }

                totalEq += lastEquity[s];
                totalCash += lastCash[s];
                totalMarketVal += lastMarketValue[s];
                totalHeldMargin += lastHeldMargin[s];
            }

            compositeBuilder.Add(new EquityPoint(k, currentUtc, totalEq, totalCash, totalMarketVal, totalHeldMargin));
        }

        ImmutableArray<EquityPoint> compositePoints = compositeBuilder.MoveToImmutable();
        decimal finalEquity = compositePoints[^1].Equity;
        decimal totalReturn = allocatedTotal > 0m ? (finalEquity - allocatedTotal) / allocatedTotal : 0m;

        DateTime effectiveInitialTimestamp = initialTimestamp ?? dates[0];
        // An arithmetic failure of the drawdown (a ratio against a tiny running high, a non-finite ratio) must not lose the composite equity and the pooled
        // statistics: the reason is returned (the same reason codes as the single backtest's diagnosticsUnavailable) and the drawdown figures stay empty.
        ImmutableArray<RankedDrawdownEpisode> episodes = ImmutableArray<RankedDrawdownEpisode>.Empty;
        string? drawdownUnavailableReason = null;
        try
        {
            episodes = DrawdownEpisodeEnumerator.Enumerate(
                allocatedTotal,
                effectiveInitialTimestamp,
                compositePoints,
                1);
        }
        catch (OverflowException)
        {
            drawdownUnavailableReason = BacktestDiagnosticReasons.ArithmeticOverflow;
        }
        catch (ArithmeticException)
        {
            drawdownUnavailableReason = BacktestDiagnosticReasons.NonFiniteResult;
        }

        RankedDrawdownEpisode? worstEpisode = episodes.Length > 0 ? episodes[0] : null;
        decimal maxDrawdownRatio = worstEpisode?.DepthRatio ?? 0m;

        return new CompositePortfolioResult(
            AllocatedTotal: allocatedTotal,
            FinalEquity: finalEquity,
            TotalReturn: totalReturn,
            MaxDrawdownRatio: maxDrawdownRatio,
            WorstEpisode: worstEpisode,
            PooledTradeCount: pooledTradeCount,
            PooledWinRate: pooledWinRate,
            CompositeEquityPoints: compositePoints,
            DrawdownUnavailableReason: drawdownUnavailableReason);
    }
}
