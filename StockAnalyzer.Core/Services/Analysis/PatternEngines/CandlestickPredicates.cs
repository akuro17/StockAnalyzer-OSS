using System;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>Prior-trend and price-zone variant per candlestick specification (<c>06_Candlestick_Patterns\*.md</c> §1, AI context §3.2).</summary>
public enum CandleTrendZoneVariant
{
    T0None = 0,
    T1ReversalBottom = 1,
    T2ReversalTop = 2,
    T3ContinuationBull = 3,
    T4ContinuationBear = 4,
    T5CrossDojiExtreme = 5,
    T6ThreeWhiteSoldiersBottom = 6,
}

/// <summary>
/// Shared v2 candlestick primitives, size/shape predicates, gaps, and T0..T6 trend/zone rules
/// (<c>00_COMMON_MATHEMATICAL_DEFINITIONS.md</c> §§1.1, 3, 4; plan ChartPatterns56 v2, run V2-8).
/// All arithmetic uses <see cref="decimal"/>; invalid bars or zero denominators yield <c>false</c>.
/// </summary>
public static class CandlestickPredicates
{
    public static decimal Body(in CandleData bar) => Math.Abs(bar.Close - bar.Open);

    public static decimal Range(in CandleData bar) => bar.High - bar.Low;

    public static decimal UpperShadow(in CandleData bar) => bar.High - Math.Max(bar.Open, bar.Close);

    public static decimal LowerShadow(in CandleData bar) => Math.Min(bar.Open, bar.Close) - bar.Low;

    public static bool IsBullish(in CandleData bar) => bar.Close > bar.Open;

    public static bool IsBearish(in CandleData bar) => bar.Close < bar.Open;

    public static decimal RealBodyHigh(in CandleData bar) => Math.Max(bar.Open, bar.Close);

    public static decimal RealBodyLow(in CandleData bar) => Math.Min(bar.Open, bar.Close);

    /// <summary>Common §3: returns false when both context denominators are non-positive.</summary>
    public static bool HasValidContext(decimal avgBody, decimal atr) => avgBody > 0m || atr > 0m;

    /// <summary>Common §3: <c>R_i &gt; 0 &amp;&amp; B_i &gt;= max(1.50*AvgBody, 0.60*ATR) &amp;&amp; B_i / R_i &gt;= 0.60</c>.</summary>
    public static bool IsLarge(in CandleData bar, decimal avgBody, decimal atr, CoreCandlePatternParameter p)
    {
        if (!HasValidContext(avgBody, atr))
        {
            return false;
        }

        decimal range = Range(in bar);
        if (range <= 0m)
        {
            return false;
        }

        decimal body = Body(in bar);
        decimal minBody = Math.Max(p.LargeBodyAvgRatio * avgBody, p.LargeBodyAtrRatio * atr);
        return body >= minBody && body / range >= p.LargeRangeRatio;
    }

    /// <summary>Common §3: <c>R_i &gt; 0 &amp;&amp; B_i &lt;= 0.80*AvgBody &amp;&amp; B_i / R_i &lt;= 0.50</c>.</summary>
    public static bool IsSmall(in CandleData bar, decimal avgBody, decimal atr, CoreCandlePatternParameter p)
    {
        if (!HasValidContext(avgBody, atr))
        {
            return false;
        }

        decimal range = Range(in bar);
        if (range <= 0m)
        {
            return false;
        }

        decimal body = Body(in bar);
        return body <= p.SmallBodyAvgRatio * avgBody && body / range <= p.SmallRangeRatio;
    }

    /// <summary>Common §3: <c>R_i &gt; 0 &amp;&amp; B_i &gt;= max(1.50*AvgBody, 0.80*ATR) &amp;&amp; B_i/R_i &gt;= 0.90 &amp;&amp; U_i &lt;= 0.05*R_i &amp;&amp; D_i &lt;= 0.05*R_i</c>.</summary>
    public static bool IsMarubozu(in CandleData bar, decimal avgBody, decimal atr, CoreCandlePatternParameter p)
    {
        if (!HasValidContext(avgBody, atr))
        {
            return false;
        }

        decimal range = Range(in bar);
        if (range <= 0m)
        {
            return false;
        }

        decimal body = Body(in bar);
        decimal upper = UpperShadow(in bar);
        decimal lower = LowerShadow(in bar);
        decimal minBody = Math.Max(p.LargeBodyAvgRatio * avgBody, PatternSpecConstants.CandleMarubozuAtrRatio * atr);
        decimal maxWick = p.WickToleranceRangeRatio * range;
        return body >= minBody
               && body / range >= PatternSpecConstants.CandleMarubozuBodyRangeMin
               && upper <= maxWick
               && lower <= maxWick;
    }

    /// <summary>Common §3 normal doji: <c>R_i &gt;= 0.50*ATR &amp;&amp; R_i &gt; 0 &amp;&amp; B_i &lt;= 0.05*R_i</c>.</summary>
    public static bool IsDojiNormal(in CandleData bar, decimal avgBody, decimal atr, CoreCandlePatternParameter p)
    {
        if (!HasValidContext(avgBody, atr))
        {
            return false;
        }

        decimal range = Range(in bar);
        if (range <= 0m)
        {
            return false;
        }

        decimal body = Body(in bar);
        return range >= PatternSpecConstants.CandleDojiNormalMinRangeAtr * atr
               && body <= p.DojiBodyRangeRatio * range;
    }

    /// <summary>Common §3 isolated-star doji (Morning/Evening Star, Abandoned Baby): <c>R_i == 0 || B_i &lt;= 0.05*R_i</c>.</summary>
    public static bool IsDojiStar(in CandleData bar, decimal avgBody, decimal atr, CoreCandlePatternParameter p)
    {
        if (!HasValidContext(avgBody, atr))
        {
            return false;
        }

        decimal range = Range(in bar);
        if (range < 0m)
        {
            return false;
        }

        return range == 0m || Body(in bar) <= p.DojiBodyRangeRatio * range;
    }

    /// <summary>Common §3 body gap up between real body high of <paramref name="prev"/> and real body low of <paramref name="next"/>.</summary>
    public static bool HasRealBodyGapUp(in CandleData prev, in CandleData next, decimal avgBody, decimal gapMinBodyRatio)
        => RealBodyLow(in next) > RealBodyHigh(in prev) + gapMinBodyRatio * avgBody;

    /// <summary>Common §3 body gap down between real body low of <paramref name="prev"/> and real body high of <paramref name="next"/>.</summary>
    public static bool HasRealBodyGapDown(in CandleData prev, in CandleData next, decimal avgBody, decimal gapMinBodyRatio)
        => RealBodyHigh(in next) < RealBodyLow(in prev) - gapMinBodyRatio * avgBody;

    /// <summary>Common §3 wick-to-wick upward gap: <c>L_{i+1} &gt; H_i</c>.</summary>
    public static bool HasWickGapUp(in CandleData prev, in CandleData next) => next.Low > prev.High;

    /// <summary>Common §3 wick-to-wick downward gap: <c>H_{i+1} &lt; L_i</c>.</summary>
    public static bool HasWickGapDown(in CandleData prev, in CandleData next) => next.High < prev.Low;

    /// <summary>Evaluates prior-trend and price-zone variant <paramref name="variant"/> for pattern interval <c>[s, t]</c> with <c>e = s - 1</c>.</summary>
    public static bool TrendAndZoneHold(
        PatternSeriesContext ctx,
        CoreCandlePatternParameter p,
        CandleTrendZoneVariant variant,
        int s,
        int t,
        int e,
        decimal atr)
    {
        return variant switch
        {
            CandleTrendZoneVariant.T0None => T0Holds(ctx, p, s, t),
            CandleTrendZoneVariant.T1ReversalBottom => T1Holds(ctx, p, s, t, e),
            CandleTrendZoneVariant.T2ReversalTop => T2Holds(ctx, p, s, t, e),
            CandleTrendZoneVariant.T3ContinuationBull => T3Holds(ctx, p, s, e, atr),
            CandleTrendZoneVariant.T4ContinuationBear => T4Holds(ctx, p, s, e, atr),
            CandleTrendZoneVariant.T5CrossDojiExtreme => T5Holds(ctx, p, t, e, atr),
            CandleTrendZoneVariant.T6ThreeWhiteSoldiersBottom => T6Holds(ctx, p, s, t, e),
            _ => false,
        };
    }

    private static bool T0Holds(PatternSeriesContext ctx, CoreCandlePatternParameter p, int s, int t)
    {
        // Spec §1 defines prior context for T0 (Marubozu / Kicking) as optional guidance rather than part of the detection predicate.
        return true;
    }

    private static bool T1Holds(PatternSeriesContext ctx, CoreCandlePatternParameter p, int s, int t, int e)
    {
        if (p.RequirePriorTrend)
        {
            int? trend = PatternMath.ShortTermTrend(ctx, e, p.PriorTrendLookbackBars, p.PriorTrendAtrMultiplier, p.AtrPeriod);
            if (trend != -1)
            {
                return false;
            }
        }

        if (p.RequirePriceZone)
        {
            decimal minLow = ctx[s].Low;
            for (int i = s + 1; i <= t; i++)
            {
                minLow = Math.Min(minLow, ctx[i].Low);
            }

            decimal? pos = PatternMath.Pos(ctx, minLow, t, p.PriceZoneLookbackBars);
            if (pos is null || pos.Value > p.PriceZoneThreshold)
            {
                return false;
            }
        }

        return true;
    }

    private static bool T2Holds(PatternSeriesContext ctx, CoreCandlePatternParameter p, int s, int t, int e)
    {
        if (p.RequirePriorTrend)
        {
            int? trend = PatternMath.ShortTermTrend(ctx, e, p.PriorTrendLookbackBars, p.PriorTrendAtrMultiplier, p.AtrPeriod);
            if (trend != 1)
            {
                return false;
            }
        }

        if (p.RequirePriceZone)
        {
            decimal maxHigh = ctx[s].High;
            for (int i = s + 1; i <= t; i++)
            {
                maxHigh = Math.Max(maxHigh, ctx[i].High);
            }

            decimal? pos = PatternMath.Pos(ctx, maxHigh, t, p.PriceZoneLookbackBars);
            if (pos is null || pos.Value < 1m - p.PriceZoneThreshold)
            {
                return false;
            }
        }

        return true;
    }

    private static bool T3Holds(PatternSeriesContext ctx, CoreCandlePatternParameter p, int s, int e, decimal atr)
    {
        if (p.RequirePriorTrend)
        {
            int slowFrom = e - PatternSpecConstants.CandleContinuationEmaSlowPeriod + 1;
            int moveFrom = e - p.PriorTrendLookbackBars;
            if (slowFrom < 0 || moveFrom < 0 || !ctx.AllValid(moveFrom, e) || !ctx.AllValid(slowFrom, e))
            {
                return false;
            }

            decimal? ema10 = ctx.Ema(PatternSpecConstants.ShortTrendEmaPeriod, e);
            decimal? ema20 = ctx.Ema(PatternSpecConstants.CandleContinuationEmaSlowPeriod, e);
            if (ema10 is null || ema20 is null)
            {
                return false;
            }

            decimal change = ctx[e].Close - ctx[moveFrom].Close;
            if (change < p.PriorTrendAtrMultiplier * atr
                || ctx[e].Close < ema20.Value
                || (ctx[e].Close <= ema10.Value && ema10.Value <= ema20.Value))
            {
                return false;
            }
        }

        if (p.RequirePriceZone)
        {
            decimal? pos = PatternMath.Pos(ctx, ctx[s].Close, s, p.PriceZoneLookbackBars);
            if (pos is null || pos.Value < 1m - p.PriceZoneThreshold)
            {
                return false;
            }
        }

        return true;
    }

    private static bool T4Holds(PatternSeriesContext ctx, CoreCandlePatternParameter p, int s, int e, decimal atr)
    {
        if (p.RequirePriorTrend)
        {
            int slowFrom = e - PatternSpecConstants.CandleContinuationEmaSlowPeriod + 1;
            int moveFrom = e - p.PriorTrendLookbackBars;
            if (slowFrom < 0 || moveFrom < 0 || !ctx.AllValid(moveFrom, e) || !ctx.AllValid(slowFrom, e))
            {
                return false;
            }

            decimal? ema10 = ctx.Ema(PatternSpecConstants.ShortTrendEmaPeriod, e);
            decimal? ema20 = ctx.Ema(PatternSpecConstants.CandleContinuationEmaSlowPeriod, e);
            if (ema10 is null || ema20 is null)
            {
                return false;
            }

            decimal change = ctx[moveFrom].Close - ctx[e].Close;
            if (change < p.PriorTrendAtrMultiplier * atr
                || ctx[e].Close > ema20.Value
                || (ctx[e].Close >= ema10.Value && ema10.Value >= ema20.Value))
            {
                return false;
            }
        }

        if (p.RequirePriceZone)
        {
            decimal? pos = PatternMath.Pos(ctx, ctx[s].Close, s, p.PriceZoneLookbackBars);
            if (pos is null || pos.Value > p.PriceZoneThreshold)
            {
                return false;
            }
        }

        return true;
    }

    private static bool T5Holds(PatternSeriesContext ctx, CoreCandlePatternParameter p, int t, int e, decimal atr)
    {
        if (p.RequirePriorTrend)
        {
            int moveFrom = e - p.PriorTrendLookbackBars;
            if (moveFrom < 0 || !ctx.AllValid(moveFrom, e))
            {
                return false;
            }

            decimal absChange = Math.Abs(ctx[e].Close - ctx[moveFrom].Close);
            if (absChange < p.PriorTrendAtrMultiplier * atr)
            {
                return false;
            }
        }

        if (p.RequirePriceZone)
        {
            decimal? posHigh = PatternMath.Pos(ctx, ctx[t].High, t, p.PriceZoneLookbackBars);
            decimal? posLow = PatternMath.Pos(ctx, ctx[t].Low, t, p.PriceZoneLookbackBars);
            bool inHigh = posHigh.HasValue && posHigh.Value >= 1m - p.PriceZoneThreshold;
            bool inLow = posLow.HasValue && posLow.Value <= p.PriceZoneThreshold;
            if (!inHigh && !inLow)
            {
                return false;
            }
        }

        return true;
    }

    private static bool T6Holds(PatternSeriesContext ctx, CoreCandlePatternParameter p, int s, int t, int e)
    {
        bool trendCheck = p.RequirePriorTrend
            && PatternMath.ShortTermTrend(ctx, e, p.PriorTrendLookbackBars, p.PriorTrendAtrMultiplier, p.AtrPeriod) == -1;

        bool zone60Check = false;
        if (p.RequirePriceZone)
        {
            decimal minLow = ctx[s].Low;
            for (int i = s + 1; i <= t; i++)
            {
                minLow = Math.Min(minLow, ctx[i].Low);
            }

            decimal? pos = PatternMath.Pos(ctx, minLow, t, p.PriceZoneLookbackBars);
            zone60Check = pos.HasValue && pos.Value <= p.PriceZoneThreshold;
        }

        if (!p.RequirePriorTrend && !p.RequirePriceZone)
        {
            return true;
        }

        if (p.RequirePriorTrend && p.RequirePriceZone)
        {
            return trendCheck || zone60Check;
        }

        return p.RequirePriorTrend ? trendCheck : zone60Check;
    }
}
