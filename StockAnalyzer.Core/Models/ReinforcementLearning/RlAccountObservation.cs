using System;
using System.Collections.Immutable;
using StockAnalyzer.Core.Models.Backtest.Engine;

namespace StockAnalyzer.Core.Models.ReinforcementLearning;

/// <summary>Single owner of the declared sixteen dimensionless decimal account fields.</summary>
public static class RlAccountObservation
{
    public static ImmutableArray<decimal> Build(decimal quantity, decimal entryPrice, decimal currentClose,
        EquityPoint point, decimal unrealized, decimal initialCapital, decimal drawdown,
        int heldBars, int remainingDecisions, bool pendingEntry, bool pendingExit,
        decimal? activeStopPrice = null, decimal retainedStopDistance = 0m, bool deferredMarketExit = false)
    {
        if (initialCapital <= 0m || currentClose <= 0m || quantity < 0m)
            throw new ArgumentOutOfRangeException(nameof(initialCapital), "A positive capital/Close and a long-only quantity are required.");
        var fields = ImmutableArray.Create(quantity > 0m ? 1m : 0m, quantity / RlContract.MaxPositionQuantity,
            point.Cash / initialCapital, point.HeldMargin / initialCapital, point.Equity / initialCapital, unrealized / initialCapital,
            pendingEntry ? 1m : 0m, pendingExit ? 1m : 0m, (decimal)heldBars / RlContract.EpisodeHorizon,
            quantity == 0m ? 0m : entryPrice / currentClose, drawdown,
            (decimal)remainingDecisions / RlContract.EpisodeHorizon, activeStopPrice.HasValue ? 1m : 0m,
            activeStopPrice / currentClose ?? 0m, retainedStopDistance, deferredMarketExit ? 1m : 0m);
        foreach (decimal field in fields)
            if (!float.IsFinite((float)field)) throw new OverflowException("Account ML observation is not finite float32.");
        return fields;
    }
}
