using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Threading;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Services.Analysis.PatternEngines;

/// <summary>
/// Single-definition v2 Candlestick Pattern engine (<c>06_Candlestick_Patterns\*.md</c>; plan ChartPatterns56 v2, runs V2-8..V2-10;
/// ledgers <c>V2-8</c>, <c>V2-9</c>, <c>V2-10</c>).
/// <para>
/// Model P-prefix (no shadowing, single-bar <c>1m</c> events at <c>t</c>, plan R-7): evaluates all 43 candlestick pattern types via a per-type rule table
/// using pre-pattern context (<c>e = t - k</c>, decision U2-02 A) and cached EMA arrays in <see cref="PatternSeriesContext"/>.
/// </para>
/// </summary>
public static class CandlestickPatternEngine
{
    private delegate bool CandlePatternRule(PatternSeriesContext ctx, CoreCandlePatternParameter p, int s, int t, int e, decimal atr, decimal avgBody);

    private sealed record CandleRuleEntry(
        int Length,
        CandleTrendZoneVariant TrendZone,
        CandlePatternRule ShapeMatches,
        CandlePatternRule? VolumeOverride = null);

    private static readonly FrozenDictionary<IndicatorType, CandleRuleEntry> Rules = BuildRules();

    /// <summary>True when <paramref name="type"/> is evaluated by <see cref="CandlestickPatternEngine"/>.</summary>
    public static bool Supports(IndicatorType type) => Rules.ContainsKey(type);

    /// <summary>Evaluates <paramref name="type"/> across <paramref name="ctx"/> (indicator nulls bars before <c>MinBars</c>).</summary>
    public static PatternBarResult[] Run(PatternSeriesContext ctx, IndicatorType type, CoreCandlePatternParameter p, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(p);
        if (!Rules.TryGetValue(type, out CandleRuleEntry? entry))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Candlestick type is not registered in CandlestickPatternEngine.");
        }

        var results = new PatternBarResult[ctx.Count];
        int minBars = p.ComputeMinBars(entry.Length);
        if (ctx.Count < minBars)
        {
            return results;
        }

        for (int t = minBars - 1; t < ctx.Count; t++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int s = t - entry.Length + 1;
            int e = s - 1;
            if (!ctx.AllValid(s, t))
            {
                continue;
            }

            if (!PatternMath.TryAtrCtx(ctx, e, p.AtrPeriod, out decimal atr, out decimal avgBody)
                || !CandlestickPredicates.HasValidContext(avgBody, atr))
            {
                continue;
            }

            if (!CandlestickPredicates.TrendAndZoneHold(ctx, p, entry.TrendZone, s, t, e, atr))
            {
                continue;
            }

            if (!entry.ShapeMatches(ctx, p, s, t, e, atr, avgBody))
            {
                continue;
            }

            bool volumeOk = entry.VolumeOverride != null
                ? entry.VolumeOverride(ctx, p, s, t, e, atr, avgBody)
                : PatternMath.VolumeConfirmed(ctx, ctx[t].Volume, e, p.RequireVolumeConfirmation, p.VolumeMultiplier);
            if (!volumeOk)
            {
                continue;
            }

            results[t] = new PatternBarResult(
                BullishFires: true,
                BearishFires: false,
                Direction: 1,
                Score: null,
                ConfirmedAtIndex: t,
                RuleCode: 0);
        }

        return results;
    }

    private static FrozenDictionary<IndicatorType, CandleRuleEntry> BuildRules()
    {
        var map = new Dictionary<IndicatorType, CandleRuleEntry>
        {
            // ---- 1-bar (9 types, V2-8)
            [IndicatorType.CandleBullishMarubozu] = new(1, CandleTrendZoneVariant.T0None,
                (ctx, p, s, t, _, atr, avgBody) =>
                {
                    CandleData c = ctx[t];
                    return CandlestickPredicates.IsBullish(in c) && CandlestickPredicates.IsMarubozu(in c, avgBody, atr, p);
                }),
            [IndicatorType.CandleBearishMarubozu] = new(1, CandleTrendZoneVariant.T0None,
                (ctx, p, s, t, _, atr, avgBody) =>
                {
                    CandleData c = ctx[t];
                    return CandlestickPredicates.IsBearish(in c) && CandlestickPredicates.IsMarubozu(in c, avgBody, atr, p);
                }),
            [IndicatorType.CandleHammer] = new(1, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, atr, avgBody) => UmbrellaMatches(ctx[t], p, atr, avgBody, longLower: true, bullishDirection: true)),
            [IndicatorType.CandleInvertedHammer] = new(1, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, atr, avgBody) => UmbrellaMatches(ctx[t], p, atr, avgBody, longLower: false, bullishDirection: true)),
            [IndicatorType.CandleHangingMan] = new(1, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, atr, avgBody) => UmbrellaMatches(ctx[t], p, atr, avgBody, longLower: true, bullishDirection: false)),
            [IndicatorType.CandleShootingStar] = new(1, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, atr, avgBody) => UmbrellaMatches(ctx[t], p, atr, avgBody, longLower: false, bullishDirection: false)),
            [IndicatorType.CandleCrossDoji] = new(1, CandleTrendZoneVariant.T5CrossDojiExtreme,
                (ctx, p, s, t, _, atr, avgBody) =>
                {
                    CandleData c = ctx[t];
                    if (!CandlestickPredicates.IsDojiNormal(in c, avgBody, atr, p))
                    {
                        return false;
                    }

                    decimal r = CandlestickPredicates.Range(in c);
                    decimal minShadow = PatternSpecConstants.CandleCrossDojiMinShadowRatio * r;
                    return CandlestickPredicates.UpperShadow(in c) > minShadow
                           && CandlestickPredicates.LowerShadow(in c) > minShadow;
                }),
            [IndicatorType.CandleDragonflyDoji] = new(1, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, atr, avgBody) => ShadowDojiMatches(ctx[t], p, atr, longLower: true)),
            [IndicatorType.CandleGravestoneDoji] = new(1, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, atr, avgBody) => ShadowDojiMatches(ctx[t], p, atr, longLower: false)),

            // ---- 2-bar (9 types, V2-8)
            [IndicatorType.CandleBullishEngulfing] = new(2, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, _, s, t, _, _, avgBody) => EngulfingMatches(ctx[s], ctx[t], avgBody, bullish: true),
                EngulfingVolume),
            [IndicatorType.CandleBearishEngulfing] = new(2, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, _, s, t, _, _, avgBody) => EngulfingMatches(ctx[s], ctx[t], avgBody, bullish: false),
                EngulfingVolume),
            [IndicatorType.CandleBullishHarami] = new(2, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, atr, avgBody) => HaramiMatches(ctx[s], ctx[t], p, atr, avgBody, bullish: true)),
            [IndicatorType.CandleBearishHarami] = new(2, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, atr, avgBody) => HaramiMatches(ctx[s], ctx[t], p, atr, avgBody, bullish: false)),
            [IndicatorType.CandlePiercingLine] = new(2, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, atr, avgBody) => PiercingOrDarkCloudMatches(ctx[s], ctx[t], p, atr, avgBody, bullish: true)),
            [IndicatorType.CandleDarkCloudCover] = new(2, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, atr, avgBody) => PiercingOrDarkCloudMatches(ctx[s], ctx[t], p, atr, avgBody, bullish: false)),
            [IndicatorType.CandleHomingPigeon] = new(2, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, atr, avgBody) =>
                {
                    CandleData c1 = ctx[s];
                    CandleData c2 = ctx[t];
                    return CandlestickPredicates.IsBearish(in c1)
                           && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
                           && CandlestickPredicates.IsBearish(in c2)
                           && CandlestickPredicates.IsSmall(in c2, avgBody, atr, p)
                           && c1.Close < c2.Close && c2.Close < c2.Open && c2.Open < c1.Open;
                }),
            [IndicatorType.CandleBullishKicking] = new(2, CandleTrendZoneVariant.T0None,
                (ctx, p, s, t, _, atr, avgBody) => KickingMatches(ctx[s], ctx[t], p, atr, avgBody, bullish: true)),
            [IndicatorType.CandleBearishKicking] = new(2, CandleTrendZoneVariant.T0None,
                (ctx, p, s, t, _, atr, avgBody) => KickingMatches(ctx[s], ctx[t], p, atr, avgBody, bullish: false)),

            // ---- 3-bar (16 types, V2-9)
            [IndicatorType.CandleMorningStar] = new(3, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, atr, avgBody) => StarMatches(ctx[s], ctx[s + 1], ctx[t], p, atr, avgBody, bullish: true)),
            [IndicatorType.CandleEveningStar] = new(3, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, atr, avgBody) => StarMatches(ctx[s], ctx[s + 1], ctx[t], p, atr, avgBody, bullish: false)),
            [IndicatorType.CandleThreeWhiteSoldiers] = new(3, CandleTrendZoneVariant.T6ThreeWhiteSoldiersBottom,
                (ctx, p, s, t, _, _, avgBody) => SoldiersOrCrowsMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody, bullish: true)),
            [IndicatorType.CandleThreeBlackCrows] = new(3, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, _, avgBody) => SoldiersOrCrowsMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody, bullish: false)),
            [IndicatorType.CandleBullishSideBySideWhiteLines] = new(3, CandleTrendZoneVariant.T3ContinuationBull,
                (ctx, p, s, t, _, _, avgBody) => SideBySideMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody, bullish: true)),
            [IndicatorType.CandleBearishSideBySideWhiteLines] = new(3, CandleTrendZoneVariant.T4ContinuationBear,
                (ctx, p, s, t, _, _, avgBody) => SideBySideMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody, bullish: false)),
            [IndicatorType.CandleBullishAbandonedBaby] = new(3, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, atr, avgBody) => AbandonedBabyMatches(ctx[s], ctx[s + 1], ctx[t], p, atr, avgBody, bullish: true)),
            [IndicatorType.CandleBearishAbandonedBaby] = new(3, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, atr, avgBody) => AbandonedBabyMatches(ctx[s], ctx[s + 1], ctx[t], p, atr, avgBody, bullish: false)),
            [IndicatorType.CandleAdvanceBlock] = new(3, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, _, avgBody) => AdvanceBlockMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody)),
            [IndicatorType.CandleDeliberation] = new(3, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, _, avgBody) => DeliberationMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody)),
            [IndicatorType.CandleStickSandwich] = new(3, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, _, avgBody) => StickSandwichMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody)),
            [IndicatorType.CandleBullishTasukiGap] = new(3, CandleTrendZoneVariant.T3ContinuationBull,
                (ctx, p, s, t, _, _, avgBody) => TasukiOrGapThreeMethodsMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody, bullish: true, gapFilled: false)),
            [IndicatorType.CandleBearishTasukiGap] = new(3, CandleTrendZoneVariant.T4ContinuationBear,
                (ctx, p, s, t, _, _, avgBody) => TasukiOrGapThreeMethodsMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody, bullish: false, gapFilled: false)),
            [IndicatorType.CandleBullishGapThreeMethods] = new(3, CandleTrendZoneVariant.T3ContinuationBull,
                (ctx, p, s, t, _, _, avgBody) => TasukiOrGapThreeMethodsMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody, bullish: true, gapFilled: true)),
            [IndicatorType.CandleBearishGapThreeMethods] = new(3, CandleTrendZoneVariant.T4ContinuationBear,
                (ctx, p, s, t, _, _, avgBody) => TasukiOrGapThreeMethodsMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody, bullish: false, gapFilled: true)),
            [IndicatorType.CandleIdenticalThreeCrows] = new(3, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, _, avgBody) => IdenticalThreeCrowsMatches(ctx[s], ctx[s + 1], ctx[t], p, avgBody)),

            // ---- 4-bar & 5-bar (9 types, V2-10)
            [IndicatorType.CandleBullishThreeLineStrike] = new(4, CandleTrendZoneVariant.T3ContinuationBull,
                (ctx, p, s, t, _, _, avgBody) => ThreeLineStrikeMatches(ctx[s], ctx[s + 1], ctx[s + 2], ctx[t], p, avgBody, bullish: true)),
            [IndicatorType.CandleBearishThreeLineStrike] = new(4, CandleTrendZoneVariant.T4ContinuationBear,
                (ctx, p, s, t, _, _, avgBody) => ThreeLineStrikeMatches(ctx[s], ctx[s + 1], ctx[s + 2], ctx[t], p, avgBody, bullish: false)),
            [IndicatorType.CandleConcealingBabySwallow] = new(4, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, atr, avgBody) => ConcealingBabySwallowMatches(ctx[s], ctx[s + 1], ctx[s + 2], ctx[t], p, atr, avgBody)),
            [IndicatorType.CandleRisingThreeMethods] = new(5, CandleTrendZoneVariant.T3ContinuationBull,
                (ctx, p, s, t, _, atr, avgBody) => ThreeMethodsMatches(ctx[s], ctx[s + 1], ctx[s + 2], ctx[s + 3], ctx[t], p, atr, avgBody, bullish: true),
                VolumeOverride: ThreeMethodsVolume),
            [IndicatorType.CandleFallingThreeMethods] = new(5, CandleTrendZoneVariant.T4ContinuationBear,
                (ctx, p, s, t, _, atr, avgBody) => ThreeMethodsMatches(ctx[s], ctx[s + 1], ctx[s + 2], ctx[s + 3], ctx[t], p, atr, avgBody, bullish: false),
                VolumeOverride: ThreeMethodsVolume),
            [IndicatorType.CandleMatHold] = new(5, CandleTrendZoneVariant.T3ContinuationBull,
                (ctx, p, s, t, _, atr, avgBody) => MatHoldMatches(ctx[s], ctx[s + 1], ctx[s + 2], ctx[s + 3], ctx[t], p, atr, avgBody)),
            [IndicatorType.CandleLadderBottom] = new(5, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, _, avgBody) => LadderBottomMatches(ctx[s], ctx[s + 1], ctx[s + 2], ctx[s + 3], ctx[t], p, avgBody)),
            [IndicatorType.CandleBullishBreakaway] = new(5, CandleTrendZoneVariant.T1ReversalBottom,
                (ctx, p, s, t, _, atr, avgBody) => BreakawayMatches(ctx[s], ctx[s + 1], ctx[s + 2], ctx[s + 3], ctx[t], p, atr, avgBody, bullish: true)),
            [IndicatorType.CandleBearishBreakaway] = new(5, CandleTrendZoneVariant.T2ReversalTop,
                (ctx, p, s, t, _, atr, avgBody) => BreakawayMatches(ctx[s], ctx[s + 1], ctx[s + 2], ctx[s + 3], ctx[t], p, atr, avgBody, bullish: false)),
        };

        return map.ToFrozenDictionary();
    }

    private static bool UmbrellaMatches(in CandleData bar, CoreCandlePatternParameter p, decimal atr, decimal avgBody, bool longLower, bool bullishDirection)
    {
        decimal range = CandlestickPredicates.Range(in bar);
        if (range <= 0m || range < PatternSpecConstants.CandleUmbrellaMinRangeAtr * atr)
        {
            return false;
        }

        decimal body = CandlestickPredicates.Body(in bar);
        if (body <= p.DojiBodyRangeRatio * range
            || body > Math.Min(PatternSpecConstants.CandleUmbrellaMaxBodyRangeRatio * range, p.SmallBodyAvgRatio * avgBody))
        {
            return false;
        }

        if (p is CoreUmbrellaPatternParameter umbrella)
        {
            if (umbrella.StrictColorMatch && (bullishDirection ? !CandlestickPredicates.IsBullish(in bar) : !CandlestickPredicates.IsBearish(in bar)))
            {
                return false;
            }

            decimal primary = longLower ? CandlestickPredicates.LowerShadow(in bar) : CandlestickPredicates.UpperShadow(in bar);
            decimal opposite = longLower ? CandlestickPredicates.UpperShadow(in bar) : CandlestickPredicates.LowerShadow(in bar);
            return primary >= umbrella.LongShadowBodyMultiplier * body
                   && opposite <= Math.Min(PatternSpecConstants.CandleUmbrellaMaxOppShadowRangeRatio * range, umbrella.OppositeShadowBodyMaxRatio * body);
        }

        decimal defPrimary = longLower ? CandlestickPredicates.LowerShadow(in bar) : CandlestickPredicates.UpperShadow(in bar);
        decimal defOpposite = longLower ? CandlestickPredicates.UpperShadow(in bar) : CandlestickPredicates.LowerShadow(in bar);
        return defPrimary >= PatternFlagParameterLimits.CandleUmbrellaLongShadowDefault * body
               && defOpposite <= Math.Min(PatternSpecConstants.CandleUmbrellaMaxOppShadowRangeRatio * range, PatternFlagParameterLimits.CandleUmbrellaOppositeShadowDefault * body);
    }

    private static bool ShadowDojiMatches(in CandleData bar, CoreCandlePatternParameter p, decimal atr, bool longLower)
    {
        decimal range = CandlestickPredicates.Range(in bar);
        if (range <= 0m || range < PatternSpecConstants.CandleShadowDojiMinRangeAtr * atr)
        {
            return false;
        }

        decimal body = CandlestickPredicates.Body(in bar);
        decimal longShadow = longLower ? CandlestickPredicates.LowerShadow(in bar) : CandlestickPredicates.UpperShadow(in bar);
        decimal shortShadow = longLower ? CandlestickPredicates.UpperShadow(in bar) : CandlestickPredicates.LowerShadow(in bar);
        return body <= p.DojiBodyRangeRatio * range
               && shortShadow <= p.WickToleranceRangeRatio * range
               && longShadow >= PatternSpecConstants.CandleShadowDojiMinLongShadowRatio * range;
    }

    private static bool EngulfingMatches(in CandleData c1, in CandleData c2, decimal avgBody, bool bullish)
    {
        if (CandlestickPredicates.Range(in c1) <= 0m || CandlestickPredicates.Range(in c2) <= 0m)
        {
            return false;
        }

        decimal b1 = CandlestickPredicates.Body(in c1);
        decimal b2 = CandlestickPredicates.Body(in c2);
        if (b1 < PatternSpecConstants.CandleEngulfingFirstMinBodyAvgRatio * avgBody
            || b2 < PatternSpecConstants.CandleEngulfingSecondMinBodyAvgRatio * avgBody
            || b2 <= b1)
        {
            return false;
        }

        return bullish
            ? CandlestickPredicates.IsBearish(in c1) && CandlestickPredicates.IsBullish(in c2)
              && c2.Open <= c1.Close && c1.Open <= c2.Close
            : CandlestickPredicates.IsBullish(in c1) && CandlestickPredicates.IsBearish(in c2)
              && c2.Close <= c1.Open && c1.Close <= c2.Open;
    }

    private static bool EngulfingVolume(PatternSeriesContext ctx, CoreCandlePatternParameter p, int s, int t, int e, decimal atr, decimal avgBody)
    {
        if (!p.RequireVolumeConfirmation)
        {
            return true;
        }

        if (!PatternMath.VolumeConfirmed(ctx, ctx[t].Volume, e, requireVolume: true, p.VolumeMultiplier))
        {
            return false;
        }

        if (!PatternMath.TryAverageVolume(ctx, e, PatternSpecConstants.VolumeAveragingBars, out decimal avgVol) || avgVol == 0m)
        {
            return true;
        }

        return ctx[t].Volume >= PatternSpecConstants.CandleEngulfingVolumeRatio * ctx[s].Volume;
    }

    private static bool HaramiMatches(in CandleData c1, in CandleData c2, CoreCandlePatternParameter p, decimal atr, decimal avgBody, bool bullish)
    {
        if (bullish)
        {
            return CandlestickPredicates.IsBearish(in c1)
                   && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
                   && CandlestickPredicates.IsBullish(in c2)
                   && CandlestickPredicates.IsSmall(in c2, avgBody, atr, p)
                   && CandlestickPredicates.Body(in c2) <= PatternSpecConstants.CandleHaramiMaxInnerBodyRatio * CandlestickPredicates.Body(in c1)
                   && c1.Close < c2.Open && c2.Close < c1.Open;
        }

        return CandlestickPredicates.IsBullish(in c1)
               && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
               && CandlestickPredicates.IsBearish(in c2)
               && CandlestickPredicates.IsSmall(in c2, avgBody, atr, p)
               && CandlestickPredicates.Body(in c2) <= PatternSpecConstants.CandleHaramiMaxInnerBodyRatio * CandlestickPredicates.Body(in c1)
               && c1.Open < c2.Close && c2.Open < c1.Close;
    }

    private static bool PiercingOrDarkCloudMatches(in CandleData c1, in CandleData c2, CoreCandlePatternParameter p, decimal atr, decimal avgBody, bool bullish)
    {
        if (CandlestickPredicates.Range(in c2) <= 0m)
        {
            return false;
        }

        decimal mid1 = (c1.Open + c1.Close) / 2m;
        if (bullish)
        {
            return CandlestickPredicates.IsBearish(in c1)
                   && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
                   && CandlestickPredicates.IsBullish(in c2)
                   && c2.Open < c1.Low
                   && c2.Close > mid1 && c2.Close < c1.Open;
        }

        return CandlestickPredicates.IsBullish(in c1)
               && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
               && CandlestickPredicates.IsBearish(in c2)
               && c2.Open > c1.High
               && c2.Close > c1.Open && c2.Close < mid1;
    }

    private static bool KickingMatches(in CandleData c1, in CandleData c2, CoreCandlePatternParameter p, decimal atr, decimal avgBody, bool bullish)
    {
        bool c1Ok = bullish
            ? CandlestickPredicates.IsBearish(in c1) && CandlestickPredicates.IsMarubozu(in c1, avgBody, atr, p)
            : CandlestickPredicates.IsBullish(in c1) && CandlestickPredicates.IsMarubozu(in c1, avgBody, atr, p);
        bool c2Ok = bullish
            ? CandlestickPredicates.IsBullish(in c2) && CandlestickPredicates.IsMarubozu(in c2, avgBody, atr, p)
            : CandlestickPredicates.IsBearish(in c2) && CandlestickPredicates.IsMarubozu(in c2, avgBody, atr, p);
        if (!c1Ok || !c2Ok)
        {
            return false;
        }

        bool requireWick = p is not CoreGapCandlePatternParameter gapParam || gapParam.RequireWickToWickGap;
        if (requireWick)
        {
            decimal minGap = Math.Max(PatternSpecConstants.CandleKickingMinGapAvgRatio * avgBody, PatternSpecConstants.CandleKickingMinGapAtrRatio * atr);
            return bullish ? c2.Low - c1.High >= minGap : c1.Low - c2.High >= minGap;
        }

        decimal bodyGapRatio = ((CoreGapCandlePatternParameter)p).GapMinBodyRatio;
        return bullish
            ? CandlestickPredicates.HasRealBodyGapUp(in c1, in c2, avgBody, bodyGapRatio)
            : CandlestickPredicates.HasRealBodyGapDown(in c1, in c2, avgBody, bodyGapRatio);
    }

    private static bool StarMatches(in CandleData c1, in CandleData c2, in CandleData c3, CoreCandlePatternParameter p, decimal atr, decimal avgBody, bool bullish)
    {
        // Note: Morning/Evening Star exempts c2 from R2 > 0 for four-price doji stars (spec §5).
        decimal r2 = CandlestickPredicates.Range(in c2);
        decimal b2 = CandlestickPredicates.Body(in c2);
        bool c2Small = b2 <= p.SmallBodyAvgRatio * avgBody && (r2 == 0m || b2 <= p.SmallRangeRatio * r2);
        if (!c2Small)
        {
            return false;
        }

        decimal r3 = CandlestickPredicates.Range(in c3);
        if (r3 <= 0m)
        {
            return false;
        }

        decimal b3 = CandlestickPredicates.Body(in c3);
        if (b3 < PatternSpecConstants.CandleStarThirdMinBodyAvgRatio * avgBody || b3 / r3 < p.LargeRangeRatio)
        {
            return false;
        }

        decimal mid1 = (c1.Open + c1.Close) / 2m;
        if (bullish)
        {
            return CandlestickPredicates.IsBearish(in c1)
                   && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
                   && CandlestickPredicates.RealBodyHigh(in c2) < c1.Close
                   && CandlestickPredicates.IsBullish(in c3)
                   && c3.Close > mid1;
        }

        return CandlestickPredicates.IsBullish(in c1)
               && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
               && CandlestickPredicates.RealBodyLow(in c2) > c1.Close
               && CandlestickPredicates.IsBearish(in c3)
               && c3.Close < mid1;
    }

    private static bool SoldiersOrCrowsMatches(in CandleData c1, in CandleData c2, in CandleData c3, CoreCandlePatternParameter p, decimal avgBody, bool bullish)
    {
        decimal maxShadowBodyRatio = p is CoreMultiBarCandlePatternParameter mb
            ? mb.MaxUpperOrLowerShadowRatio
            : PatternFlagParameterLimits.CandleMultiBarMaxShadowRatioDefault;
        decimal minBody = PatternSpecConstants.CandleThreeSoldiersMinBodyAvgRatio * avgBody;

        CandleData[] seq = { c1, c2, c3 };
        foreach (CandleData bar in seq)
        {
            decimal r = CandlestickPredicates.Range(in bar);
            decimal b = CandlestickPredicates.Body(in bar);
            if (r <= 0m || b < minBody || b / r < PatternSpecConstants.CandleThreeSoldiersMinBodyRange)
            {
                return false;
            }

            if (bullish)
            {
                if (!CandlestickPredicates.IsBullish(in bar) || CandlestickPredicates.UpperShadow(in bar) > maxShadowBodyRatio * b)
                {
                    return false;
                }
            }
            else
            {
                if (!CandlestickPredicates.IsBearish(in bar) || CandlestickPredicates.LowerShadow(in bar) > maxShadowBodyRatio * b)
                {
                    return false;
                }
            }
        }

        return bullish
            ? c1.Close < c2.Close && c2.Close < c3.Close
              && c1.High < c2.High && c2.High < c3.High
              && c1.Open <= c2.Open && c2.Open <= c1.Close
              && c2.Open <= c3.Open && c3.Open <= c2.Close
            : c1.Close > c2.Close && c2.Close > c3.Close
              && c1.Low > c2.Low && c2.Low > c3.Low
              && c1.Close <= c2.Open && c2.Open <= c1.Open
              && c2.Close <= c3.Open && c3.Open <= c2.Open;
    }

    private static bool SideBySideMatches(in CandleData c1, in CandleData c2, in CandleData c3, CoreCandlePatternParameter p, decimal avgBody, bool bullish)
    {
        if (CandlestickPredicates.Range(in c1) <= 0m || CandlestickPredicates.Range(in c2) <= 0m || CandlestickPredicates.Range(in c3) <= 0m)
        {
            return false;
        }

        if (CandlestickPredicates.Body(in c1) < PatternSpecConstants.CandleGapPatternFirstMinBodyAvgRatio * avgBody)
        {
            return false;
        }

        // Spec 06/23 & 24: both c2 and c3 are white (bullish) lines gapping in the trend direction!
        if (!CandlestickPredicates.IsBullish(in c2) || !CandlestickPredicates.IsBullish(in c3))
        {
            return false;
        }

        decimal gapRatio = p is CoreGapCandlePatternParameter gp ? gp.GapMinBodyRatio : PatternFlagParameterLimits.CandleGapMinBodyRatioDefault;
        bool requireWick = p is CoreGapCandlePatternParameter gpw && gpw.RequireWickToWickGap;
        decimal b2 = CandlestickPredicates.Body(in c2);
        decimal b3 = CandlestickPredicates.Body(in c3);
        decimal openTol = PatternSpecConstants.CandleSideBySideOpenTolRatio * Math.Max(avgBody, b2);
        decimal bodyTol = PatternSpecConstants.CandleSideBySideBodyTolRatio * b2;

        bool similarC3 = Math.Abs(c3.Open - c2.Open) <= openTol && Math.Abs(b3 - b2) <= bodyTol;
        if (!similarC3)
        {
            return false;
        }

        if (bullish)
        {
            return CandlestickPredicates.IsBullish(in c1)
                   && c2.Open > c1.Close + gapRatio * avgBody
                   && (!requireWick || CandlestickPredicates.HasWickGapUp(in c1, in c2))
                   && c3.Open > c1.Close;
        }

        return CandlestickPredicates.IsBearish(in c1)
               && CandlestickPredicates.HasRealBodyGapDown(in c1, in c2, avgBody, gapRatio)
               && c2.Close < c1.Close
               && (!requireWick || CandlestickPredicates.HasWickGapDown(in c1, in c2))
               && c3.Close < c1.Close;
    }

    private static bool AbandonedBabyMatches(in CandleData c1, in CandleData c2, in CandleData c3, CoreCandlePatternParameter p, decimal atr, decimal avgBody, bool bullish)
    {
        if (!CandlestickPredicates.IsDojiStar(in c2, avgBody, atr, p))
        {
            return false;
        }

        decimal gapRatio = p is CoreGapCandlePatternParameter gp ? gp.GapMinBodyRatio : PatternFlagParameterLimits.CandleGapMinBodyRatioDefault;
        bool requireWick = p is not CoreGapCandlePatternParameter gpw || gpw.RequireWickToWickGap;
        decimal mid1 = (c1.Open + c1.Close) / 2m;

        if (bullish)
        {
            bool gap1 = requireWick ? CandlestickPredicates.HasWickGapDown(in c1, in c2) : CandlestickPredicates.HasRealBodyGapDown(in c1, in c2, avgBody, gapRatio);
            bool gap2 = requireWick ? CandlestickPredicates.HasWickGapUp(in c2, in c3) : CandlestickPredicates.HasRealBodyGapUp(in c2, in c3, avgBody, gapRatio);
            return CandlestickPredicates.IsBearish(in c1) && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
                   && CandlestickPredicates.IsBullish(in c3) && CandlestickPredicates.IsLarge(in c3, avgBody, atr, p)
                   && gap1 && gap2
                   && c3.Close > mid1;
        }

        bool gapUp = requireWick ? CandlestickPredicates.HasWickGapUp(in c1, in c2) : CandlestickPredicates.HasRealBodyGapUp(in c1, in c2, avgBody, gapRatio);
        bool gapDn = requireWick ? CandlestickPredicates.HasWickGapDown(in c2, in c3) : CandlestickPredicates.HasRealBodyGapDown(in c2, in c3, avgBody, gapRatio);
        return CandlestickPredicates.IsBullish(in c1) && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
               && CandlestickPredicates.IsBearish(in c3) && CandlestickPredicates.IsLarge(in c3, avgBody, atr, p)
               && gapUp && gapDn
               && c3.Close < mid1;
    }

    private static bool AdvanceBlockMatches(in CandleData c1, in CandleData c2, in CandleData c3, CoreCandlePatternParameter p, decimal avgBody)
    {
        if (CandlestickPredicates.Range(in c1) <= 0m || CandlestickPredicates.Range(in c2) <= 0m || CandlestickPredicates.Range(in c3) <= 0m)
        {
            return false;
        }

        if (!CandlestickPredicates.IsBullish(in c1) || !CandlestickPredicates.IsBullish(in c2) || !CandlestickPredicates.IsBullish(in c3))
        {
            return false;
        }

        if (c1.Close >= c2.Close || c2.Close >= c3.Close || c1.High >= c2.High || c2.High >= c3.High)
        {
            return false;
        }

        if (c2.Open < c1.Open || c2.Open > c1.Close || c3.Open < c2.Open || c3.Open > c2.Close)
        {
            return false;
        }

        decimal noiseMin = p is CoreMultiBarCandlePatternParameter mb ? mb.NoiseBodyMinRatio : PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioDefault;
        decimal b1 = CandlestickPredicates.Body(in c1);
        decimal b2 = CandlestickPredicates.Body(in c2);
        decimal b3 = CandlestickPredicates.Body(in c3);
        if (b1 < noiseMin * avgBody || b1 <= b2 || b2 <= b3 || b3 > PatternSpecConstants.CandleAdvanceBlockMaxThirdBodyRatio * b1)
        {
            return false;
        }

        decimal u1 = CandlestickPredicates.UpperShadow(in c1);
        decimal u2 = CandlestickPredicates.UpperShadow(in c2);
        decimal u3 = CandlestickPredicates.UpperShadow(in c3);
        return (u2 >= PatternSpecConstants.CandleAdvanceBlockMinUpperShadowBodyRatio * b2
                || u3 >= PatternSpecConstants.CandleAdvanceBlockMinUpperShadowBodyRatio * b3)
               && u3 > u1;
    }

    private static bool DeliberationMatches(in CandleData c1, in CandleData c2, in CandleData c3, CoreCandlePatternParameter p, decimal avgBody)
    {
        if (CandlestickPredicates.Range(in c1) <= 0m || CandlestickPredicates.Range(in c2) <= 0m || CandlestickPredicates.Range(in c3) <= 0m)
        {
            return false;
        }

        decimal minB12 = PatternSpecConstants.CandleThreeSoldiersMinBodyAvgRatio * avgBody;
        decimal b1 = CandlestickPredicates.Body(in c1);
        decimal b2 = CandlestickPredicates.Body(in c2);
        decimal b3 = CandlestickPredicates.Body(in c3);
        decimal r3 = CandlestickPredicates.Range(in c3);

        bool firstTwoOk = CandlestickPredicates.IsBullish(in c1) && CandlestickPredicates.IsBullish(in c2)
                          && b1 >= minB12 && b2 >= minB12
                          && c2.Close > c1.Close && c1.Open <= c2.Open && c2.Open <= c1.Close;
        if (!firstTwoOk)
        {
            return false;
        }

        bool c3SignOrDoji = c3.Close >= c3.Open || b3 <= p.DojiBodyRangeRatio * r3;
        return c3SignOrDoji
               && b3 <= Math.Min(PatternSpecConstants.CandleDeliberationMaxThirdBodyRatio * b2, p.SmallBodyAvgRatio * avgBody)
               && c3.Open >= c2.Close - PatternSpecConstants.CandleDeliberationThirdOpenSlackAvgRatio * avgBody;
    }

    private static bool StickSandwichMatches(in CandleData c1, in CandleData c2, in CandleData c3, CoreCandlePatternParameter p, decimal avgBody)
    {
        if (CandlestickPredicates.Range(in c1) <= 0m || CandlestickPredicates.Range(in c2) <= 0m || CandlestickPredicates.Range(in c3) <= 0m)
        {
            return false;
        }

        decimal noiseMin = p is CoreMultiBarCandlePatternParameter mb ? mb.NoiseBodyMinRatio : PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioDefault;
        if (!CandlestickPredicates.IsBearish(in c1) || CandlestickPredicates.Body(in c1) < noiseMin * avgBody)
        {
            return false;
        }

        if (!CandlestickPredicates.IsBullish(in c2) || c2.Open <= c1.Close || c2.Close <= c1.Close)
        {
            return false;
        }

        decimal closeTol = Math.Max(PatternSpecConstants.CandleEqualPriceAvgBodyRatio * avgBody, PatternSpecConstants.CandleEqualPriceFraction * c1.Close);
        return CandlestickPredicates.IsBearish(in c3)
               && c3.Open >= c2.Close - PatternSpecConstants.CandleStickSandwichThirdOpenSlackAvgRatio * avgBody
               && c3.Close <= c2.Open
               && Math.Abs(c3.Close - c1.Close) <= closeTol;
    }

    private static bool TasukiOrGapThreeMethodsMatches(in CandleData c1, in CandleData c2, in CandleData c3, CoreCandlePatternParameter p, decimal avgBody, bool bullish, bool gapFilled)
    {
        if (CandlestickPredicates.Range(in c1) <= 0m || CandlestickPredicates.Range(in c2) <= 0m || CandlestickPredicates.Range(in c3) <= 0m)
        {
            return false;
        }

        if (CandlestickPredicates.Body(in c1) < PatternSpecConstants.CandleGapPatternFirstMinBodyAvgRatio * avgBody)
        {
            return false;
        }

        decimal gapRatio = p is CoreGapCandlePatternParameter gp ? gp.GapMinBodyRatio : PatternFlagParameterLimits.CandleGapMinBodyRatioDefault;
        bool requireWick = p is CoreGapCandlePatternParameter gpw && gpw.RequireWickToWickGap;
        decimal slack = PatternSpecConstants.CandleGapReentryOpenSlackAvgRatio * avgBody;

        if (bullish)
        {
            bool firstTwo = CandlestickPredicates.IsBullish(in c1) && CandlestickPredicates.IsBullish(in c2)
                            && c2.Open > c1.Close + gapRatio * avgBody
                            && (!requireWick || CandlestickPredicates.HasWickGapUp(in c1, in c2));
            if (!firstTwo || !CandlestickPredicates.IsBearish(in c3) || c3.Open < c2.Open || c3.Open > c2.Close + slack)
            {
                return false;
            }

            return gapFilled ? c1.Open <= c3.Close && c3.Close <= c1.Close : c1.Close < c3.Close && c3.Close < c2.Open;
        }

        bool firstTwoBear = CandlestickPredicates.IsBearish(in c1) && CandlestickPredicates.IsBearish(in c2)
                            && c2.Open < c1.Close - gapRatio * avgBody
                            && (!requireWick || CandlestickPredicates.HasWickGapDown(in c1, in c2));
        if (!firstTwoBear || !CandlestickPredicates.IsBullish(in c3) || c3.Open < c2.Close - slack || c3.Open > c2.Open)
        {
            return false;
        }

        return gapFilled ? c1.Close <= c3.Close && c3.Close <= c1.Open : c2.Open < c3.Close && c3.Close < c1.Close;
    }

    private static bool IdenticalThreeCrowsMatches(in CandleData c1, in CandleData c2, in CandleData c3, CoreCandlePatternParameter p, decimal avgBody)
    {
        if (CandlestickPredicates.Range(in c1) <= 0m || CandlestickPredicates.Range(in c2) <= 0m || CandlestickPredicates.Range(in c3) <= 0m)
        {
            return false;
        }

        decimal noiseMin = p is CoreMultiBarCandlePatternParameter mb ? mb.NoiseBodyMinRatio : PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioDefault;
        decimal b1 = CandlestickPredicates.Body(in c1);
        decimal b2 = CandlestickPredicates.Body(in c2);
        decimal b3 = CandlestickPredicates.Body(in c3);
        if (!CandlestickPredicates.IsBearish(in c1) || !CandlestickPredicates.IsBearish(in c2) || !CandlestickPredicates.IsBearish(in c3))
        {
            return false;
        }

        decimal minBody = noiseMin * avgBody;
        if (b1 < minBody || b2 < minBody || b3 < minBody || c1.Close <= c2.Close || c2.Close <= c3.Close)
        {
            return false;
        }

        decimal tol12 = Math.Max(PatternSpecConstants.CandleEqualPriceAvgBodyRatio * avgBody, PatternSpecConstants.CandleEqualPriceFraction * c1.Close);
        decimal tol23 = Math.Max(PatternSpecConstants.CandleEqualPriceAvgBodyRatio * avgBody, PatternSpecConstants.CandleEqualPriceFraction * c2.Close);
        if (Math.Abs(c2.Open - c1.Close) > tol12 || Math.Abs(c3.Open - c2.Close) > tol23)
        {
            return false;
        }

        return CandlestickPredicates.LowerShadow(in c1) <= PatternSpecConstants.CandleIdenticalCrowsMaxLowerShadowBodyRatio * b1
               && CandlestickPredicates.LowerShadow(in c2) <= PatternSpecConstants.CandleIdenticalCrowsMaxLowerShadowBodyRatio * b2
               && CandlestickPredicates.LowerShadow(in c3) <= PatternSpecConstants.CandleIdenticalCrowsMaxLowerShadowBodyRatio * b3;
    }

    private static bool ThreeMethodsMatches(
        in CandleData c1, in CandleData c2, in CandleData c3, in CandleData c4, in CandleData c5,
        CoreCandlePatternParameter p, decimal atr, decimal avgBody, bool bullish)
    {
        bool c1Ok = bullish
            ? CandlestickPredicates.IsBullish(in c1) && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
            : CandlestickPredicates.IsBearish(in c1) && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p);
        if (!c1Ok
            || !CandlestickPredicates.IsSmall(in c2, avgBody, atr, p)
            || !CandlestickPredicates.IsSmall(in c3, avgBody, atr, p)
            || !CandlestickPredicates.IsSmall(in c4, avgBody, atr, p))
        {
            return false;
        }

        int counterBars = 0;
        if (!TryThreeMethodsMidBar(in c2, in c1, bullish, ref counterBars)
            || !TryThreeMethodsMidBar(in c3, in c1, bullish, ref counterBars)
            || !TryThreeMethodsMidBar(in c4, in c1, bullish, ref counterBars))
        {
            return false;
        }

        bool directionPullback = bullish
            ? counterBars >= 2 || c4.Close < c2.Close
            : counterBars >= 2 || c4.Close > c2.Close;
        if (!directionPullback || CandlestickPredicates.Range(in c5) <= 0m)
        {
            return false;
        }

        bool c5Strong = CandlestickPredicates.Body(in c5) >= PatternSpecConstants.CandleThreeMethodsFifthMinBodyAvgRatio * avgBody
                        || CandlestickPredicates.IsLarge(in c5, avgBody, atr, p);
        if (!c5Strong)
        {
            return false;
        }

        return bullish
            ? CandlestickPredicates.IsBullish(in c5) && c5.Open >= c4.Close && c5.Close > c1.High
            : CandlestickPredicates.IsBearish(in c5) && c5.Open <= c4.Close && c5.Close < c1.Low;
    }

    private static bool TryThreeMethodsMidBar(in CandleData bar, in CandleData c1, bool bullish, ref int counterBars)
    {
        if (bar.High > c1.High || bar.Low < c1.Low)
        {
            return false;
        }

        if (bullish)
        {
            if (CandlestickPredicates.RealBodyLow(in bar) < c1.Open)
            {
                return false;
            }

            if (CandlestickPredicates.IsBearish(in bar))
            {
                counterBars++;
            }

            return true;
        }

        if (CandlestickPredicates.RealBodyHigh(in bar) > c1.Open)
        {
            return false;
        }

        if (CandlestickPredicates.IsBullish(in bar))
        {
            counterBars++;
        }

        return true;
    }

    private static bool ThreeMethodsVolume(PatternSeriesContext ctx, CoreCandlePatternParameter p, int s, int t, int e, decimal atr, decimal avgBody)
    {
        if (!p.RequireVolumeConfirmation)
        {
            return true;
        }

        if (!PatternMath.VolumeConfirmed(ctx, ctx[t].Volume, e, requireVolume: true, p.VolumeMultiplier))
        {
            return false;
        }

        if (!PatternMath.TryAverageVolume(ctx, e, PatternSpecConstants.VolumeAveragingBars, out decimal avgVol) || avgVol == 0m)
        {
            return true;
        }

        return ctx[s + 1].Volume + ctx[s + 2].Volume + ctx[s + 3].Volume < 3m * ctx[s].Volume;
    }

    private static bool MatHoldMatches(
        in CandleData c1, in CandleData c2, in CandleData c3, in CandleData c4, in CandleData c5,
        CoreCandlePatternParameter p, decimal atr, decimal avgBody)
    {
        if (!CandlestickPredicates.IsBullish(in c1) || !CandlestickPredicates.IsLarge(in c1, avgBody, atr, p))
        {
            return false;
        }

        if (!CandlestickPredicates.IsSmall(in c2, avgBody, atr, p)
            || c2.Open <= c1.Close + PatternSpecConstants.CandleMatHoldSecondOpenGapAvgRatio * avgBody
            || !CandlestickPredicates.IsSmall(in c3, avgBody, atr, p)
            || !CandlestickPredicates.IsSmall(in c4, avgBody, atr, p))
        {
            return false;
        }

        decimal mid1 = (c1.Open + c1.Close) / 2m;
        bool hasBear = CandlestickPredicates.IsBearish(in c2) || CandlestickPredicates.IsBearish(in c3) || CandlestickPredicates.IsBearish(in c4);
        if (!hasBear
            || c2.Low < c1.Open || c3.Low < c1.Open || c4.Low < c1.Open
            || c2.Close <= mid1 || c3.Close <= mid1 || c4.Close <= mid1)
        {
            return false;
        }

        if (CandlestickPredicates.Range(in c5) <= 0m || !CandlestickPredicates.IsBullish(in c5))
        {
            return false;
        }

        decimal maxHigh = Math.Max(Math.Max(c1.High, c2.High), Math.Max(c3.High, c4.High));
        bool c5Strong = CandlestickPredicates.Body(in c5) >= PatternSpecConstants.CandleThreeMethodsFifthMinBodyAvgRatio * avgBody
                        || CandlestickPredicates.IsLarge(in c5, avgBody, atr, p);
        return c5Strong && c5.Open >= c4.Close && c5.Close > maxHigh;
    }

    private static bool ThreeLineStrikeMatches(
        in CandleData c1, in CandleData c2, in CandleData c3, in CandleData c4,
        CoreCandlePatternParameter p, decimal avgBody, bool bullish)
    {
        decimal noiseMin = p is CoreMultiBarCandlePatternParameter mb ? mb.NoiseBodyMinRatio : PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioDefault;
        decimal minBody123 = noiseMin * avgBody;

        CandleData[] firstThree = { c1, c2, c3 };
        foreach (CandleData bar in firstThree)
        {
            if (CandlestickPredicates.Range(in bar) <= 0m
                || CandlestickPredicates.Body(in bar) < minBody123
                || (bullish ? !CandlestickPredicates.IsBullish(in bar) : !CandlestickPredicates.IsBearish(in bar)))
            {
                return false;
            }
        }

        if (CandlestickPredicates.Range(in c4) <= 0m)
        {
            return false;
        }

        decimal slack = PatternSpecConstants.CandleThreeLineStrikeOpenSlackAvgRatio * avgBody;
        decimal minB4 = PatternSpecConstants.CandleThreeLineStrikeFourthMinBodyAvgRatio * avgBody;
        decimal b4 = CandlestickPredicates.Body(in c4);

        if (bullish)
        {
            return c1.Close < c2.Close && c2.Close < c3.Close
                   && c1.Open <= c2.Open && c2.Open <= c1.Close
                   && c2.Open <= c3.Open && c3.Open <= c2.Close
                   && CandlestickPredicates.IsBearish(in c4)
                   && c4.Open >= c3.Close - slack
                   && c4.Close < c1.Open
                   && b4 >= c3.Close - c1.Open
                   && b4 >= minB4;
        }

        return c1.Close > c2.Close && c2.Close > c3.Close
               && c1.Close <= c2.Open && c2.Open <= c1.Open
               && c2.Close <= c3.Open && c3.Open <= c2.Open
               && CandlestickPredicates.IsBullish(in c4)
               && c4.Open <= c3.Close + slack
               && c4.Close > c1.Open
               && b4 >= c1.Open - c3.Close
               && b4 >= minB4;
    }

    private static bool LadderBottomMatches(
        in CandleData c1, in CandleData c2, in CandleData c3, in CandleData c4, in CandleData c5,
        CoreCandlePatternParameter p, decimal avgBody)
    {
        decimal noiseMin = p is CoreMultiBarCandlePatternParameter mb ? mb.NoiseBodyMinRatio : PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioDefault;
        decimal minBody123 = noiseMin * avgBody;

        CandleData[] firstThree = { c1, c2, c3 };
        foreach (CandleData bar in firstThree)
        {
            if (CandlestickPredicates.Range(in bar) <= 0m
                || !CandlestickPredicates.IsBearish(in bar)
                || CandlestickPredicates.Body(in bar) < minBody123)
            {
                return false;
            }
        }

        bool stepDown = c1.Close > c2.Close && c2.Close > c3.Close
                        && c1.Low > c2.Low && c2.Low > c3.Low
                        && c1.Close <= c2.Open && c2.Open <= c1.Open
                        && c2.Close <= c3.Open && c3.Open <= c2.Open;
        if (!stepDown || CandlestickPredicates.Range(in c4) <= 0m || CandlestickPredicates.Range(in c5) <= 0m)
        {
            return false;
        }

        decimal b4 = CandlestickPredicates.Body(in c4);
        bool c4Wick = (c4.Open <= c3.Open || c4.Close <= c3.Open)
                      && (b4 == 0m
                          || CandlestickPredicates.UpperShadow(in c4) >= b4
                          || CandlestickPredicates.LowerShadow(in c4) >= b4);
        if (!c4Wick)
        {
            return false;
        }

        return CandlestickPredicates.IsBullish(in c5)
               && c5.Open > CandlestickPredicates.RealBodyHigh(in c4)
               && c5.Close > c4.High
               && CandlestickPredicates.Body(in c5) >= PatternSpecConstants.CandleThreeMethodsFifthMinBodyAvgRatio * avgBody;
    }

    private static bool BreakawayMatches(
        in CandleData c1, in CandleData c2, in CandleData c3, in CandleData c4, in CandleData c5,
        CoreCandlePatternParameter p, decimal atr, decimal avgBody, bool bullish)
    {
        if (CandlestickPredicates.Range(in c2) <= 0m || CandlestickPredicates.Range(in c5) <= 0m)
        {
            return false;
        }

        decimal gapRatio = p is CoreGapCandlePatternParameter gp ? gp.GapMinBodyRatio : PatternFlagParameterLimits.CandleGapMinBodyRatioDefault;
        bool requireWick = p is CoreGapCandlePatternParameter gpw && gpw.RequireWickToWickGap;
        decimal minC5 = PatternSpecConstants.CandleBreakawayFifthMinBodyAvgRatio * avgBody;
        bool c5Strong = CandlestickPredicates.Body(in c5) >= minC5 || CandlestickPredicates.IsLarge(in c5, avgBody, atr, p);
        if (!c5Strong
            || !CandlestickPredicates.IsSmall(in c3, avgBody, atr, p)
            || !CandlestickPredicates.IsSmall(in c4, avgBody, atr, p))
        {
            return false;
        }

        if (bullish)
        {
            bool gapOk = c2.Open < c1.Close - gapRatio * avgBody
                         && (!requireWick || CandlestickPredicates.HasWickGapDown(in c1, in c2));
            bool midOk = Math.Max(c3.Close, c4.Close) < c2.Open
                         && !(c4.Low > c3.Low && c4.Close > c2.Close);
            decimal breakTarget = requireWick
                ? Math.Max(c2.High, Math.Max(c3.High, c4.High))
                : Math.Max(CandlestickPredicates.RealBodyHigh(in c2), Math.Max(CandlestickPredicates.RealBodyHigh(in c3), CandlestickPredicates.RealBodyHigh(in c4)));
            return CandlestickPredicates.IsBearish(in c1) && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
                   && CandlestickPredicates.IsBearish(in c2) && gapOk
                   && midOk
                   && CandlestickPredicates.IsBullish(in c5)
                   && c5.Close > breakTarget
                   && c2.Open < c5.Close && c5.Close < c1.Close;
        }

        bool bearGapOk = c2.Open > c1.Close + gapRatio * avgBody
                         && (!requireWick || CandlestickPredicates.HasWickGapUp(in c1, in c2));
        bool bearMidOk = Math.Min(c3.Close, c4.Close) > c2.Open
                         && !(c4.High < c3.High && c4.Close < c2.Close);
        decimal bearBreakTarget = requireWick
            ? Math.Min(c2.Low, Math.Min(c3.Low, c4.Low))
            : Math.Min(CandlestickPredicates.RealBodyLow(in c2), Math.Min(CandlestickPredicates.RealBodyLow(in c3), CandlestickPredicates.RealBodyLow(in c4)));
        return CandlestickPredicates.IsBullish(in c1) && CandlestickPredicates.IsLarge(in c1, avgBody, atr, p)
               && CandlestickPredicates.IsBullish(in c2) && bearGapOk
               && bearMidOk
               && CandlestickPredicates.IsBearish(in c5)
               && c5.Close < bearBreakTarget
               && c1.Close < c5.Close && c5.Close < c2.Open;
    }

    private static bool ConcealingBabySwallowMatches(
        in CandleData c1, in CandleData c2, in CandleData c3, in CandleData c4,
        CoreCandlePatternParameter p, decimal atr, decimal avgBody)
    {
        if (!CandlestickPredicates.IsBearish(in c1) || !CandlestickPredicates.IsMarubozu(in c1, avgBody, atr, p)
            || !CandlestickPredicates.IsBearish(in c2) || !CandlestickPredicates.IsMarubozu(in c2, avgBody, atr, p)
            || c2.Open > c1.Open || c2.Close >= c1.Close)
        {
            return false;
        }

        decimal r3 = CandlestickPredicates.Range(in c3);
        decimal b3 = CandlestickPredicates.Body(in c3);
        decimal r4 = CandlestickPredicates.Range(in c4);
        if (r3 <= 0m || r4 <= 0m)
        {
            return false;
        }

        bool c3Ok = CandlestickPredicates.IsBearish(in c3)
                    && b3 >= PatternSpecConstants.CandleConcealingBabyThirdMinBodyAvgRatio * avgBody
                    && c3.Open < c2.Close
                    && CandlestickPredicates.LowerShadow(in c3) <= PatternSpecConstants.CandleConcealingBabyThirdMaxLowerShadowRangeRatio * r3
                    && CandlestickPredicates.UpperShadow(in c3) >= PatternSpecConstants.CandleConcealingBabyThirdMinUpperShadowBodyRatio * b3
                    && c3.High > c2.Close;
        if (!c3Ok)
        {
            return false;
        }

        decimal noiseMin = p is CoreMultiBarCandlePatternParameter mb ? mb.NoiseBodyMinRatio : PatternFlagParameterLimits.CandleMultiBarNoiseBodyMinRatioDefault;
        return CandlestickPredicates.IsBearish(in c4)
               && CandlestickPredicates.Body(in c4) >= noiseMin * avgBody
               && c4.Open > c3.High
               && c4.Close < c3.Low;
    }
}
