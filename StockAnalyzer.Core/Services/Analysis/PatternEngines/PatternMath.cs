using System;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// The pure functions of the chart-pattern specification (<c>00_COMMON_MATHEMATICAL_DEFINITIONS.md</c>) that every pattern engine shares: the OHLC integrity test (1.1), the context ATR and
/// average body (2.2), the average volume and its confirmation rule (2.3), the short-term and mid-term prior trend (4.1, 4.2) and the price-zone percentile (4.3). All arithmetic is <see cref="decimal"/>.
/// <para>
/// Every function that reads a window of bars returns "not computable" (<c>false</c> or <c>null</c>) when the window leaves the series or contains an OHLC-inconsistent bar (plan R-16, the read-set
/// rule): a pattern that depends on a bad bar is "not met", it never throws and never reads around the bad bar.
/// </para>
/// The context ATR and average body are the existing <see cref="CandlePatternDetector.ComputeContext"/> (same formula, one definition); this class only adds the bounds and the validity guard.
/// </summary>
public static class PatternMath
{
    /// <summary>Spec 1.1: H &gt;= max(O,C), L &lt;= min(O,C), H &gt;= L, every price above zero, and non-negative volume (V &gt;= 0).</summary>
    public static bool IsOhlcConsistent(in CandleData bar)
        => bar.Open > 0m && bar.High > 0m && bar.Low > 0m && bar.Close > 0m
           && bar.Volume >= 0
           && bar.High >= Math.Max(bar.Open, bar.Close)
           && bar.Low <= Math.Min(bar.Open, bar.Close)
           && bar.High >= bar.Low;

    /// <summary>
    /// Spec 2.2: the average true range and the average body over the <paramref name="period"/> bars ending at <paramref name="contextEnd"/> (the bar before the pattern). Computable only when
    /// <c>contextEnd &gt;= period</c> (so every true range has a previous close) and every bar of <c>[contextEnd - period, contextEnd]</c> is consistent.
    /// </summary>
    public static bool TryAtrCtx(PatternSeriesContext ctx, int contextEnd, int period, out decimal atr, out decimal avgBody)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        atr = 0m;
        avgBody = 0m;
        if (period <= 0 || contextEnd < period || contextEnd >= ctx.Count || !ctx.AllValid(contextEnd - period, contextEnd))
        {
            return false;
        }

        if (ctx.TryGetContextStats(contextEnd, period, out avgBody, out atr))
        {
            return true;
        }

        (avgBody, atr) = CandlePatternDetector.ComputeContext(ctx.Bars, contextEnd - period + 1, contextEnd);
        return true;
    }

    /// <summary>Spec 2.3: the mean volume of the <paramref name="length"/> bars ending at <paramref name="end"/>; false when the window leaves the series or holds an inconsistent bar.</summary>
    public static bool TryAverageVolume(PatternSeriesContext ctx, int end, int length, out decimal average)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        average = 0m;
        if (length <= 0 || end - length + 1 < 0 || !ctx.AllValid(end - length + 1, end))
        {
            return false;
        }

        decimal sum = 0m;
        for (int i = end - length + 1; i <= end; i++)
        {
            long v = ctx[i].Volume;
            if (v < 0)
            {
                average = 0m;
                return false;
            }

            sum += v;
        }

        average = sum / length;
        return true;
    }

    /// <summary>
    /// Spec 2.3 volume rule: the option off, or an instrument without volume (average 0 with non-negative confirming volume), passes; with the option on and a positive average the bar needs a
    /// positive volume of at least <paramref name="multiplier"/> times the average of the <paramref name="averagingBars"/> bars ending at <paramref name="averageEnd"/>. Negative volume or an average
    /// that cannot be computed fails the rule.
    /// </summary>
    public static bool VolumeConfirmed(PatternSeriesContext ctx, long volume, int averageEnd, bool requireVolume, decimal multiplier, int averagingBars = PatternSpecConstants.VolumeAveragingBars)
    {
        if (!requireVolume)
        {
            return true;
        }

        if (volume < 0 || !TryAverageVolume(ctx, averageEnd, averagingBars, out decimal average) || average < 0m)
        {
            return false;
        }

        if (average == 0m)
        {
            return true;
        }

        return volume > 0 && volume >= multiplier * average;
    }

    /// <summary>
    /// The break margin <c>m</c> of a close breakout (decision U2-08 A, plan R-20): with <paramref name="marginPercent"/> = 0 the margin is 0 (a strict inequality, the ATR term is off too); above 0 it is
    /// <c>max(marginPercent / 100 * level, atrMultiplier * ATR_ctx(bar - 1, 14))</c>. Null when the ATR cannot be computed (the break is then not met).
    /// </summary>
    public static decimal? BreakMargin(PatternSeriesContext ctx, decimal marginPercent, decimal atrMultiplier, decimal level, int bar)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (marginPercent == 0m)
        {
            return 0m;
        }

        if (!TryAtrCtx(ctx, bar - 1, PatternSpecConstants.AtrPeriod, out decimal atr, out _))
        {
            return null;
        }

        return Math.Max(marginPercent / 100m * level, atrMultiplier * atr);
    }

    /// <summary>
    /// Spec 4.3: the position of <paramref name="price"/> inside the range of the <paramref name="bars"/> bars ending at <paramref name="end"/>, from 0 (the lowest low) to 1 (the highest high), clamped.
    /// Null (undefined: the pattern is not met) when the window leaves the series, holds an inconsistent bar, or is flat (highest high equals lowest low).
    /// </summary>
    public static decimal? Pos(PatternSeriesContext ctx, decimal price, int end, int bars)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        int start = end - bars + 1;
        if (bars <= 0 || start < 0 || !ctx.AllValid(start, end))
        {
            return null;
        }

        decimal high = ctx[start].High;
        decimal low = ctx[start].Low;
        for (int i = start + 1; i <= end; i++)
        {
            high = Math.Max(high, ctx[i].High);
            low = Math.Min(low, ctx[i].Low);
        }

        if (high <= low)
        {
            return null;
        }

        return Math.Clamp((price - low) / (high - low), 0m, 1m);
    }

    /// <summary>
    /// Spec 4.1: the short-term prior trend before a pattern whose context ends at <paramref name="contextEnd"/>: +1 up, -1 down, 0 none. Up needs a close change over
    /// <paramref name="lookbackBars"/> bars of at least <paramref name="atrMultiplier"/> context ATRs, the close above the EMA and the EMA above itself <see cref="PatternSpecConstants.ShortTrendEmaShift"/> bars earlier;
    /// down is the mirror. Null when it cannot be computed (not enough bars, an inconsistent bar in the read set, no ATR).
    /// </summary>
    public static int? ShortTermTrend(PatternSeriesContext ctx, int contextEnd, int lookbackBars, decimal atrMultiplier, int atrPeriod = PatternSpecConstants.AtrPeriod)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        const int emaPeriod = PatternSpecConstants.ShortTrendEmaPeriod;
        const int emaShift = PatternSpecConstants.ShortTrendEmaShift;

        int emaStart = contextEnd - emaShift - emaPeriod + 1;
        if (lookbackBars <= 0 || contextEnd - lookbackBars < 0 || emaStart < 0
            || !ctx.AllValid(contextEnd - lookbackBars, contextEnd) || !ctx.AllValid(emaStart, contextEnd)
            || !TryAtrCtx(ctx, contextEnd, atrPeriod, out decimal atr, out _))
        {
            return null;
        }

        decimal? ema = ctx.Ema(emaPeriod, contextEnd);
        decimal? emaBefore = ctx.Ema(emaPeriod, contextEnd - emaShift);
        if (ema is null || emaBefore is null)
        {
            return null;
        }

        decimal close = ctx[contextEnd].Close;
        decimal change = close - ctx[contextEnd - lookbackBars].Close;
        decimal required = atrMultiplier * atr;

        if (change >= required && close > ema.Value && ema.Value > emaBefore.Value)
        {
            return 1;
        }

        if (-change >= required && close < ema.Value && ema.Value < emaBefore.Value)
        {
            return -1;
        }

        return 0;
    }

    /// <summary>
    /// Spec 4.2: a prior up-trend before a pattern that starts at <paramref name="patternStart"/>, measured on <c>[patternStart - lookbackBars, patternStart - 1]</c> (the start bar is excluded). The move from the
    /// lowest low of that window to <paramref name="referencePrice"/> must be at least <paramref name="minFraction"/> of the low or <paramref name="atrMultiplier"/> context ATRs, and the fast SMA must be above the slow SMA
    /// or above itself <see cref="PatternSpecConstants.SmaShift"/> bars earlier. False when not computable.
    /// </summary>
    public static bool MidTermUptrend(PatternSeriesContext ctx, int patternStart, decimal referencePrice, int lookbackBars, decimal minFraction, decimal atrMultiplier, int atrPeriod = PatternSpecConstants.AtrPeriod,
        MidTrendConfirmation confirmation = MidTrendConfirmation.FastAboveSlowOrRising)
        => MidTermTrend(ctx, patternStart, referencePrice, lookbackBars, minFraction, atrMultiplier, atrPeriod, confirmation, up: true);

    /// <summary>Spec 4.2 mirror of <see cref="MidTermUptrend"/>: a prior down-trend measured from the highest high of the window down to <paramref name="referencePrice"/>.</summary>
    public static bool MidTermDowntrend(PatternSeriesContext ctx, int patternStart, decimal referencePrice, int lookbackBars, decimal minFraction, decimal atrMultiplier, int atrPeriod = PatternSpecConstants.AtrPeriod,
        MidTrendConfirmation confirmation = MidTrendConfirmation.FastAboveSlowOrRising)
        => MidTermTrend(ctx, patternStart, referencePrice, lookbackBars, minFraction, atrMultiplier, atrPeriod, confirmation, up: false);

    private static bool MidTermTrend(PatternSeriesContext ctx, int patternStart, decimal referencePrice, int lookbackBars, decimal minFraction, decimal atrMultiplier, int atrPeriod, MidTrendConfirmation confirmation, bool up)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        int from = patternStart - lookbackBars;
        int last = patternStart - 1;
        if (lookbackBars <= 0 || from < 0 || !ctx.AllValid(from, last))
        {
            return false;
        }

        decimal extreme = up ? ctx[from].Low : ctx[from].High;
        for (int i = from + 1; i <= last; i++)
        {
            extreme = up ? Math.Min(extreme, ctx[i].Low) : Math.Max(extreme, ctx[i].High);
        }

        if (extreme <= 0m)
        {
            return false;
        }

        decimal move = up ? referencePrice - extreme : extreme - referencePrice;
        bool moveBig = move / extreme >= minFraction;
        if (!moveBig && TryAtrCtx(ctx, last, atrPeriod, out decimal atr, out _))
        {
            moveBig = move >= atrMultiplier * atr;
        }

        if (!moveBig)
        {
            return false;
        }

        decimal? fast = ctx.Sma(PatternSpecConstants.SmaFastPeriod, last);
        decimal? slow = ctx.Sma(PatternSpecConstants.SmaSlowPeriod, last);
        decimal? fastBefore = ctx.Sma(PatternSpecConstants.SmaFastPeriod, last - PatternSpecConstants.SmaShift);

        bool againstSlow = fast.HasValue && slow.HasValue && (up ? fast.Value > slow.Value : fast.Value < slow.Value);
        bool againstSelf = fast.HasValue && fastBefore.HasValue && (up ? fast.Value > fastBefore.Value : fast.Value < fastBefore.Value);
        return confirmation switch
        {
            MidTrendConfirmation.FastAboveSlow => againstSlow,
            MidTrendConfirmation.FastRising => againstSelf,
            _ => againstSlow || againstSelf,
        };
    }
}

/// <summary>
/// Which moving-average condition completes the mid-term prior trend of spec 4.2. The common text allows either one; a family file (Head and Shoulders: the slow SMA only; Double Top / Bottom: the fast SMA
/// against itself only) states one of them, and that more specific text is used by its engine (plan ledger V2-3, interpretation I-3).
/// </summary>
public enum MidTrendConfirmation
{
    /// <summary>Spec 4.2 as written: the fast SMA beyond the slow SMA OR beyond itself <see cref="PatternSpecConstants.SmaShift"/> bars earlier.</summary>
    FastAboveSlowOrRising = 0,

    /// <summary>Only the fast SMA against the slow SMA (up: fast above slow; down: fast below slow).</summary>
    FastAboveSlow = 1,

    /// <summary>Only the fast SMA against itself <see cref="PatternSpecConstants.SmaShift"/> bars earlier.</summary>
    FastRising = 2,
}
