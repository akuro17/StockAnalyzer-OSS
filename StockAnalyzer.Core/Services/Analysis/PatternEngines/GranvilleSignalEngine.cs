using System;
using System.Threading;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>Per-bar result of <see cref="GranvilleSignalEngine"/>: independent rule flags 1..4, their logical OR (<see cref="Main"/>), and priority <see cref="RuleCode"/>.</summary>
public readonly record struct GranvilleBarResult(bool Rule1, bool Rule2, bool Rule3, bool Rule4, int RuleCode)
{
    public bool Main => Rule1 || Rule2 || Rule3 || Rule4;
}

/// <summary>
/// Granville's Law Buy (rules 1–4) and Sell (rules 1–4) independent engines (<c>04_Trading_Rules\01_granvilles_law_buy_signal.md</c>, <c>02_granvilles_law_sell_signal.md</c>;
/// plan ChartPatterns56 v2, run V2-7; ledger <c>docs/sa_pattern_literal_ledger_V2-7_ElliottGranville</c>).
/// <para>
/// Model P: Buy and Sell are completely independent (neither suppresses the other). Each bar from <c>t &gt;= MaPeriod + SlopePeriod</c> evaluates all four rules independently (which by plan R-12 / CF-V2-16
/// are pairwise mutually exclusive when <c>RequirePrevBarOnMaSideForRule3</c> is enabled) and emits <c>Rule1..Rule4</c>, <c>Main</c>, and <c>RuleCode</c>.
/// Non-positive <c>SMA</c> or an OHLC-inconsistent bar in the SMA window yields <c>0</c> without throwing (plan R-16, R-17).
/// </para>
/// </summary>
public static class GranvilleSignalEngine
{
    /// <summary>Evaluates Buy rules 1..4 across <paramref name="ctx"/>.</summary>
    public static GranvilleBarResult[] RunBuy(PatternSeriesContext ctx, CoreGranvilleSignalParameter p, CancellationToken cancellationToken = default)
        => Run(ctx, p, buy: true, cancellationToken);

    /// <summary>Evaluates Sell rules 1..4 across <paramref name="ctx"/>.</summary>
    public static GranvilleBarResult[] RunSell(PatternSeriesContext ctx, CoreGranvilleSignalParameter p, CancellationToken cancellationToken = default)
        => Run(ctx, p, buy: false, cancellationToken);

    private static GranvilleBarResult[] Run(PatternSeriesContext ctx, CoreGranvilleSignalParameter p, bool buy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);

        var results = new GranvilleBarResult[ctx.Count];
        int minBars = p.MaPeriod + p.SlopePeriod + 1;
        if (ctx.Count < minBars)
        {
            return results;
        }

        int sign = buy ? 1 : -1;

        for (int t = minBars - 1; t < ctx.Count; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ctx.IsValid(t) || !ctx.IsValid(t - 1))
            {
                continue;
            }

            decimal? smaTNullable = ctx.Sma(p.MaPeriod, t);
            decimal? smaPrevNullable = ctx.Sma(p.MaPeriod, t - 1);
            decimal? smaSlopeBackNullable = ctx.Sma(p.MaPeriod, t - p.SlopePeriod);
            if (smaTNullable is not { } smaT || smaPrevNullable is not { } smaPrev || smaSlopeBackNullable is not { } smaSlopeBack)
            {
                continue;
            }

            if (smaT <= 0m || smaPrev <= 0m)
            {
                continue;
            }

            decimal slope = smaT - smaSlopeBack;
            decimal slopePct = Math.Abs(slope) / smaT * 100m;
            bool isMaRising = slope > 0m && slopePct > p.FlatThreshold;
            bool isMaFalling = slope < 0m && slopePct > p.FlatThreshold;
            bool isMaFlat = !isMaRising && !isMaFalling;

            decimal close = ctx[t].Close;
            decimal prevClose = ctx[t - 1].Close;
            decimal open = ctx[t].Open;
            decimal high = ctx[t].High;
            decimal low = ctx[t].Low;

            decimal dev = (close - smaT) / smaT * 100m;
            bool crossedAbove = prevClose <= smaPrev && close > smaT;
            bool crossedBelow = prevClose >= smaPrev && close < smaT;

            bool r1;
            bool r2;
            bool r3;
            bool r4;

            if (buy)
            {
                r1 = p.EnableRule1
                     && (isMaFlat || isMaRising)
                     && crossedAbove
                     && PatternMath.VolumeConfirmed(ctx, ctx[t].Volume, t - 1, p.RequireVolumeOnRule1, PatternSpecConstants.GranvilleRule1VolumeMultiplier);

                r2 = p.EnableRule2
                     && isMaRising
                     && crossedBelow;

                decimal proxLow = Math.Abs(low - smaT) / smaT * 100m;
                r3 = p.EnableRule3
                     && isMaRising
                     && (!p.RequirePrevBarOnMaSideForRule3 || prevClose > smaPrev)
                     && close > smaT
                     && proxLow <= p.BounceTolerance
                     && close > low;

                r4 = p.EnableRule4
                     && isMaFalling
                     && dev < -p.DeviationThreshold
                     && (!p.Rule4FirstEntryOrReversalOnly || BuyRule4Trigger(ctx, t, prevClose, smaPrev, close, open, p.DeviationThreshold));
            }
            else
            {
                r1 = p.EnableRule1
                     && (isMaFlat || isMaFalling)
                     && crossedBelow
                     && PatternMath.VolumeConfirmed(ctx, ctx[t].Volume, t - 1, p.RequireVolumeOnRule1, PatternSpecConstants.GranvilleRule1VolumeMultiplier);

                r2 = p.EnableRule2
                     && isMaFalling
                     && crossedAbove;

                decimal proxHigh = Math.Abs(high - smaT) / smaT * 100m;
                r3 = p.EnableRule3
                     && isMaFalling
                     && (!p.RequirePrevBarOnMaSideForRule3 || prevClose < smaPrev)
                     && close < smaT
                     && proxHigh <= p.BounceTolerance
                     && close < high;

                r4 = p.EnableRule4
                     && isMaRising
                     && dev > p.DeviationThreshold
                     && (!p.Rule4FirstEntryOrReversalOnly || SellRule4Trigger(ctx, t, prevClose, smaPrev, close, open, p.DeviationThreshold));
            }

            // Spec 5 priority order: 4 -> 1 -> 2 -> 3 (matches independent flags under R-12).
            int ruleCode = r4 ? 4 * sign
                : r1 ? 1 * sign
                : r2 ? 2 * sign
                : r3 ? 3 * sign
                : 0;

            results[t] = new GranvilleBarResult(r1, r2, r3, r4, ruleCode);
        }

        return results;
    }

    /// <summary>Plan R-27: FirstEntry (<c>dev_{t-1}</c> defined and <c>&gt;= -threshold</c>) OR reversal candle (<c>C_t &gt; O_t</c>).</summary>
    private static bool BuyRule4Trigger(PatternSeriesContext ctx, int t, decimal prevClose, decimal smaPrev, decimal close, decimal open, decimal threshold)
    {
        bool reversal = close > open;
        if (reversal)
        {
            return true;
        }

        // smaPrev > 0 and ctx.IsValid(t - 1) are already verified by the caller; dev_{t-1} is defined.
        decimal prevDev = (prevClose - smaPrev) / smaPrev * 100m;
        return prevDev >= -threshold;
    }

    /// <summary>Plan R-27 mirror: FirstEntry (<c>dev_{t-1}</c> defined and <c>&lt;= +threshold</c>) OR reversal candle (<c>C_t &lt; O_t</c>).</summary>
    private static bool SellRule4Trigger(PatternSeriesContext ctx, int t, decimal prevClose, decimal smaPrev, decimal close, decimal open, decimal threshold)
    {
        bool reversal = close < open;
        if (reversal)
        {
            return true;
        }

        decimal prevDev = (prevClose - smaPrev) / smaPrev * 100m;
        return prevDev <= threshold;
    }
}
