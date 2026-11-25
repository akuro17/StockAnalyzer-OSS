using System;
using System.Threading;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// Bullish / Bearish Flag of <c>01_Pattern_Recognition\03_bullish_bearish_flag.md</c> (plan ChartPatterns56 v2, run V2-4; ledger <c>sa_pattern_literal_ledger_V2-4_Flag.md</c>; decision D-V2-9 = U2-09 A, plan R-21).
/// <para>
/// Model P. At every bar <c>t</c> the bar is a possible break bar: every flag of <c>n</c> bars (the interval <c>[t - n, t - 1]</c>, which starts with the pole end bar) behind it and every pole of <c>L</c> bars
/// ending there is a candidate (at most (MaxFlagBars - MinFlagBars + 1) x (MaxPoleBars - MinPoleBars + 1) pairs, 273 with the defaults). The checks run from the cheap to the expensive: the pole (memoised per
/// pole end and length, because it does not depend on <c>t</c>), the direction of the break candle and the volume, the retrace depth and the lengths, and only then the two Theil-Sen rails
/// (<see cref="RobustRegressionMath.TryFit"/>, the shared type 7 median), the R-squared, the slopes, the touches, the channel width and the break itself.
/// Of the candidates that meet every rule the one with the largest mean normalised R-squared is taken (ties: the shorter flag, then the shorter pole); its key (direction, pole start, pole end) is consumed,
/// so one pole never signals twice. The score is that R-squared. The result at <c>t</c> reads bars up to <c>t</c> only; a bar that is not OHLC-consistent inside a candidate's read set makes it "not met" (plan R-16).
/// </para>
/// </summary>
public static class FlagEngine
{
    private readonly record struct PoleKey(int Direction, int Start, int End);

    /// <summary>The pole of one (end, length): <see cref="Width"/> is the pole range, <see cref="StartExtreme"/> its start extreme (low for a bullish pole), <see cref="EndExtreme"/> its end extreme.</summary>
    private readonly record struct Pole(bool Computed, bool Valid, decimal StartExtreme, decimal EndExtreme, decimal Width, decimal MeanVolume);

    private readonly record struct Candidate(decimal Score, int Start, int End);

    /// <summary>Runs the engine over the series of <paramref name="ctx"/>; one result per bar (the indicator makes the bars before the minimum history null).</summary>
    public static PatternBarResult[] Run(PatternSeriesContext ctx, CoreFlagPatternParameter p, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);

        int count = ctx.Count;
        var timeline = new PatternEventTimeline<PoleKey>(count);
        bool allowBull = PatternEngineSupport.AllowsBullish(p.DirectionMode);
        bool allowBear = PatternEngineSupport.AllowsBearish(p.DirectionMode);

        var volumePrefix = new decimal[count + 1];
        for (int i = 0; i < count; i++)
        {
            volumePrefix[i + 1] = volumePrefix[i] + ctx[i].Volume;
        }

        var bullishPoles = new Pole[]?[count];
        var bearishPoles = new Pole[]?[count];
        decimal[] highs = new decimal[RobustRegressionMath.MaxWindowLength];
        decimal[] lows = new decimal[RobustRegressionMath.MaxWindowLength];
        decimal[] scratch = new decimal[RobustRegressionMath.ScratchLength(RobustRegressionMath.MaxWindowLength, siegel: false)];

        for (int t = 0; t < count; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ctx.IsValid(t))
            {
                continue;
            }

            if (allowBull && ctx[t].Close > ctx[t].Open)
            {
                Evaluate(ctx, p, timeline, bullishPoles, volumePrefix, highs, lows, scratch, t, bullish: true);
            }

            if (allowBear && ctx[t].Close < ctx[t].Open)
            {
                Evaluate(ctx, p, timeline, bearishPoles, volumePrefix, highs, lows, scratch, t, bullish: false);
            }
        }

        PatternBarResult[] results = timeline.Resolve(allowBull, allowBear);
        PatternEngineSupport.ClearInconsistentBars(ctx, results);
        return results;
    }

    private static void Evaluate(PatternSeriesContext ctx, CoreFlagPatternParameter p, PatternEventTimeline<PoleKey> timeline, Pole[]?[] poles, decimal[] volumePrefix,
        decimal[] highs, decimal[] lows, decimal[] scratch, int t, bool bullish)
    {
        int direction = bullish ? 1 : -1;
        Candidate? best = null;
        decimal runningLow = decimal.MaxValue;
        decimal runningHigh = decimal.MinValue;

        for (int flagBars = 1; flagBars <= p.MaxFlagBars; flagBars++)
        {
            int end = t - flagBars;
            if (end < 0 || !ctx.IsValid(end))
            {
                break; // the flag interval [end, t - 1] cannot be read any further back (plan R-16)
            }

            runningLow = Math.Min(runningLow, ctx[end].Low);
            runningHigh = Math.Max(runningHigh, ctx[end].High);
            if (flagBars < p.MinFlagBars)
            {
                continue;
            }

            for (int poleBars = p.MinPoleBars; poleBars <= p.MaxPoleBars; poleBars++)
            {
                int start = end - poleBars + 1;
                int totalBars = t - start;
                if (start < 0 || totalBars < PatternSpecConstants.FlagTotalBarsMin || totalBars > PatternSpecConstants.FlagTotalBarsMax
                    || flagBars > PatternSpecConstants.FlagToPoleRatioMax * poleBars)
                {
                    continue;
                }

                // The memoised pole test first (an array read); the consumed set (a hash lookup) only for the rare valid pole.
                Pole pole = GetPole(ctx, p, poles, end, poleBars, bullish);
                if (!pole.Valid || timeline.IsConsumed(new PoleKey(direction, start, end)))
                {
                    continue;
                }

                if (!VolumeMet(ctx, p, volumePrefix, pole, end, t))
                {
                    continue;
                }

                decimal depth = bullish ? pole.EndExtreme - runningLow : runningHigh - pole.EndExtreme;
                decimal retrace = depth / pole.Width;
                if (retrace < p.MinRetraceRatio || retrace > p.MaxRetraceRatio)
                {
                    continue;
                }

                decimal? score = RailsMeet(ctx, p, highs, lows, scratch, t, end, flagBars, pole, bullish);
                if (score.HasValue && (best is null || score.Value > best.Value.Score))
                {
                    best = new Candidate(score.Value, start, end);
                }
            }
        }

        if (best.HasValue && timeline.TryFire(new PoleKey(direction, best.Value.Start, best.Value.End), direction, t, p.SignalPersistenceBars, best.Value.Score, t, out _))
        {
            // The flag has no hold-period invalidation (interpretation F-4 of ledger V2-4).
        }
    }

    /// <summary>Spec 2: the pole of <paramref name="poleBars"/> bars ending at <paramref name="end"/>; computed once per (direction, end, length).</summary>
    private static Pole GetPole(PatternSeriesContext ctx, CoreFlagPatternParameter p, Pole[]?[] poles, int end, int poleBars, bool bullish)
    {
        Pole[]? row = poles[end];
        if (row is null)
        {
            row = new Pole[p.MaxPoleBars + 1];
            poles[end] = row;
        }

        if (row[poleBars].Computed)
        {
            return row[poleBars];
        }

        Pole pole = ComputePole(ctx, p, end, poleBars, bullish);
        row[poleBars] = pole;
        return pole;
    }

    private static Pole ComputePole(PatternSeriesContext ctx, CoreFlagPatternParameter p, int end, int poleBars, bool bullish)
    {
        var invalid = new Pole(true, false, 0m, 0m, 0m, 0m);
        int start = end - poleBars + 1;
        if (start < 0 || !ctx.AllValid(start, end))
        {
            return invalid;
        }

        // The cheapest tests first (most candidates fail the percent move): the move, the direction share, then the ATR window and the zone.
        decimal startExtreme = bullish ? ctx[start].Low : ctx[start].High;
        decimal endExtreme = bullish ? ctx[end].High : ctx[end].Low;
        decimal width = bullish ? endExtreme - startExtreme : startExtreme - endExtreme;
        if (startExtreme <= 0m || width <= 0m || width / startExtreme < p.MinPoleMovePercent / 100m)
        {
            return invalid;
        }

        int directional = 0;
        decimal volumeSum = 0m;
        for (int i = start; i <= end; i++)
        {
            if (bullish ? ctx[i].Close > ctx[i].Open : ctx[i].Close < ctx[i].Open)
            {
                directional++;
            }

            volumeSum += ctx[i].Volume;
        }

        if ((decimal)directional / poleBars < p.MinPoleBodyRatio
            || !PatternMath.TryAtrCtx(ctx, start - 1, PatternSpecConstants.AtrPeriod, out decimal atr, out _) || width < p.MinPoleAtrMultiplier * atr)
        {
            return invalid;
        }

        decimal? position = PatternMath.Pos(ctx, endExtreme, end, PatternSpecConstants.FlagZoneBars);
        if (position is null || (bullish ? position.Value < PatternSpecConstants.FlagZoneThreshold : position.Value > 1m - PatternSpecConstants.FlagZoneThreshold))
        {
            return invalid;
        }

        return new Pole(true, true, startExtreme, endExtreme, width, volumeSum / poleBars);
    }

    /// <summary>Spec 5: the flag's mean volume is below 85 percent of the pole's and the break bar has at least 130 percent of the flag's mean; the option off or an instrument without volume passes.</summary>
    private static bool VolumeMet(PatternSeriesContext ctx, CoreFlagPatternParameter p, decimal[] volumePrefix, Pole pole, int end, int t)
    {
        if (!p.RequireVolumeConfirmation || pole.MeanVolume == 0m)
        {
            return true;
        }

        decimal flagMean = (volumePrefix[t] - volumePrefix[end]) / (t - end);
        long volume = ctx[t].Volume;
        return flagMean < PatternSpecConstants.FlagVolumeContractionRatio * pole.MeanVolume
               && volume > 0 && volume >= PatternSpecConstants.FlagBreakoutVolumeRatio * flagMean;
    }

    /// <summary>
    /// Spec 4 and 5 on the flag <c>[end, t - 1]</c>: both rails, the fit, the parallelism, the touches, the slope direction, the channel width and the break at <paramref name="t"/>.
    /// Returns the mean normalised R-squared (the score) or null when any rule fails.
    /// </summary>
    private static decimal? RailsMeet(PatternSeriesContext ctx, CoreFlagPatternParameter p, decimal[] highs, decimal[] lows, decimal[] scratch, int t, int end, int flagBars, Pole pole, bool bullish)
    {
        if (!PatternMath.TryAtrCtx(ctx, end - 1, PatternSpecConstants.AtrPeriod, out decimal atr, out _) || atr <= 0m
            || !PatternMath.TryAtrCtx(ctx, t - 1, PatternSpecConstants.AtrPeriod, out decimal atrBeforeBreak, out _))
        {
            return null;
        }

        for (int k = 0; k < flagBars; k++)
        {
            highs[k] = ctx[end + k].High;
            lows[k] = ctx[end + k].Low;
        }

        var upperWindow = new ReadOnlySpan<decimal>(highs, 0, flagBars);
        var lowerWindow = new ReadOnlySpan<decimal>(lows, 0, flagBars);
        if (!RobustRegressionMath.TryFit(upperWindow, siegel: false, scratch, out RobustLine upper) || !RobustRegressionMath.TryFit(lowerWindow, siegel: false, scratch, out RobustLine lower))
        {
            return null;
        }

        // Spec 5: the break closes beyond the rail (extrapolated to bar t, offset flagBars) by the margin, in the direction of the pole.
        decimal rail = bullish ? upper.Intercept + upper.Slope * flagBars : lower.Intercept + lower.Slope * flagBars;
        if (rail <= 0m)
        {
            return null;
        }

        decimal margin = Math.Max(PatternSpecConstants.FlagBreakoutFraction * rail, PatternSpecConstants.FlagBreakoutAtrMultiplier * atrBeforeBreak);
        decimal close = ctx[t].Close;
        if (bullish ? close <= rail + margin : close >= rail - margin)
        {
            return null;
        }

        // Spec 4: slope direction, parallelism, channel width.
        decimal limit = PatternSpecConstants.FlagRailSlopeLimitAtr;
        if (bullish ? upper.Slope / atr > limit : lower.Slope / atr < -limit)
        {
            return null;
        }

        if (Math.Abs(upper.Slope - lower.Slope) / atr > p.MaxSlopeDiffAtr)
        {
            return null;
        }

        decimal widthSum = 0m;
        for (int k = 0; k < flagBars; k++)
        {
            widthSum += (upper.Intercept + upper.Slope * k) - (lower.Intercept + lower.Slope * k);
        }

        if (widthSum / flagBars > p.MaxChannelWidthRatio * pole.Width)
        {
            return null;
        }

        // Spec 4: fit quality and independent touches of each rail.
        decimal r2Upper = NormalizedRSquared(upperWindow, upper, atr);
        decimal r2Lower = NormalizedRSquared(lowerWindow, lower, atr);
        decimal meanR2 = (r2Upper + r2Lower) / 2m;
        if (meanR2 < p.MinRSquared || Touches(upperWindow, upper, atr) < p.MinTouchCount || Touches(lowerWindow, lower, atr) < p.MinTouchCount)
        {
            return null;
        }

        return meanR2;
    }

    /// <summary>Spec 4: <c>max(0, 1 - SSres / max(SStot, n (0.5 ATR)^2))</c> of the values against the line (offset 0 = the oldest bar of the flag).</summary>
    private static decimal NormalizedRSquared(ReadOnlySpan<decimal> values, RobustLine line, decimal atr)
    {
        decimal mean = 0m;
        foreach (decimal v in values)
        {
            mean += v;
        }

        mean /= values.Length;
        decimal ssRes = 0m;
        decimal ssTot = 0m;
        for (int k = 0; k < values.Length; k++)
        {
            decimal residual = values[k] - (line.Intercept + line.Slope * k);
            ssRes += residual * residual;
            decimal deviation = values[k] - mean;
            ssTot += deviation * deviation;
        }

        decimal floor = PatternSpecConstants.FlagR2FloorAtrMultiplier * atr;
        decimal denominator = Math.Max(ssTot, values.Length * floor * floor);
        return denominator <= 0m ? 0m : Math.Max(0m, 1m - ssRes / denominator);
    }

    /// <summary>Spec 4: the number of independent touches: bars within <c>max(1 percent of the rail, 0.35 ATR)</c> of the rail, a new one only at least two bars after the previous counted one.</summary>
    private static int Touches(ReadOnlySpan<decimal> values, RobustLine line, decimal atr)
    {
        int count = 0;
        int previous = int.MinValue / 2;
        for (int k = 0; k < values.Length; k++)
        {
            decimal rail = line.Intercept + line.Slope * k;
            decimal band = Math.Max(PatternSpecConstants.FlagTouchBandFraction * rail, PatternSpecConstants.FlagTouchBandAtr * atr);
            if (Math.Abs(values[k] - rail) <= band && k - previous >= PatternSpecConstants.FlagTouchMinGapBars)
            {
                count++;
                previous = k;
            }
        }

        return count;
    }
}
