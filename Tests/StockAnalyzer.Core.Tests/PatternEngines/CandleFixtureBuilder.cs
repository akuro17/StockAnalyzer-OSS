using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Tests.PatternEngines;

/// <summary>
/// Deterministic real <see cref="CandleData"/> fixture builder for candlestick v2 engine tests
/// (MP V2-8: one 40-bar context generator + caller-listed pattern bars; no legacy fixture reuse).
/// </summary>
internal static class CandleFixtureBuilder
{
    public const int DefaultContextBars = 40;
    public const long DefaultVolume = 1000L;

    /// <summary>
    /// Builds 40 valid preamble bars with exact predictable <c>AvgBody_ctx = 2m</c>, <c>ATR_ctx = 4m</c>
    /// (steady step +/- 2m with 1m wicks on both sides -> every TR = 4m, every Body = 2m), ending with
    /// the direction requested (<paramref name="trendDirection"/>: -1 down, +1 up, 0 flat-oscillating range [180..220]).
    /// </summary>
    public static List<CandleData> BuildContextAndAppend(
        int trendDirection,
        params CandleData[] patternBars)
    {
        var bars = new List<CandleData>(DefaultContextBars + patternBars.Length);
        decimal close;
        if (trendDirection == -1)
        {
            // Steady decline from 280 down by 2 per bar -> ends at C_39 = 200.
            // Over [29..39], C_39 - C_29 = -20; -change = 20 >= 1.5 * 4 = 6, C < EMA10, EMA10 falling!
            close = 280m;
            for (int i = 0; i < DefaultContextBars; i++)
            {
                decimal open = close;
                close = open - 2m;
                bars.Add(PatternEngineTestBars.Bar(i, open, open + 1m, close - 1m, close, DefaultVolume));
            }
        }
        else if (trendDirection == 1)
        {
            // Steady rally from 120 up by 2 per bar -> ends at C_39 = 200.
            // Over [29..39], C_39 - C_29 = +20 >= 1.5 * 4 = 6, C > EMA10, EMA10 rising!
            close = 120m;
            for (int i = 0; i < DefaultContextBars; i++)
            {
                decimal open = close;
                close = open + 2m;
                bars.Add(PatternEngineTestBars.Bar(i, open, close + 1m, open - 1m, close, DefaultVolume));
            }
        }
        else
        {
            // Flat / sideways zigzag centered around 200: AvgBody = 2, ATR = 4, C_39 - C_29 = 0 (no trend).
            for (int i = 0; i < DefaultContextBars; i++)
            {
                bool up = (i % 2) == 0;
                decimal open = up ? 199m : 201m;
                decimal end = up ? 201m : 199m;
                decimal high = Math.Max(open, end) + 1m;
                decimal low = Math.Min(open, end) - 1m;
                bars.Add(PatternEngineTestBars.Bar(i, open, high, low, end, DefaultVolume));
            }
        }

        for (int j = 0; j < patternBars.Length; j++)
        {
            CandleData pb = patternBars[j];
            bars.Add(pb with { Timestamp = PatternEngineTestBars.Bar(bars.Count, pb.Open, pb.High, pb.Low, pb.Close, pb.Volume).Timestamp });
        }

        return bars;
    }

    public static CandleData MakeBar(decimal open, decimal high, decimal low, decimal close, long volume = DefaultVolume)
        => PatternEngineTestBars.Bar(0, open, high, low, close, volume);

    /// <summary>Builds a 42- or 43-bar deterministic tail guaranteed to fire <paramref name="type"/> under <see cref="FormationFixtures.RelaxedCandle"/>.</summary>
    public static CandleData[] Part1Tail(IndicatorType type) => type switch
    {
        IndicatorType.CandleBearishMarubozu
            => BuildContextAndAppend(0, MakeBar(206m, 206.2m, 200.8m, 201m), MakeBar(210m, 210.4m, 199.6m, 200m, 1300L)).ToArray(),
        IndicatorType.CandleBullishKicking
            => BuildContextAndAppend(0, MakeBar(206m, 206.2m, 200.8m, 201m), MakeBar(208.4m, 214m, 208.2m, 213.8m, 1300L)).ToArray(),
        IndicatorType.CandleBearishKicking
            => BuildContextAndAppend(0, MakeBar(201m, 206.2m, 200.8m, 206m), MakeBar(198.6m, 198.8m, 193m, 193.2m, 1300L)).ToArray(),
        IndicatorType.CandleDragonflyDoji
            => BuildContextAndAppend(-1, MakeBar(199m, 199.1m, 195m, 199.1m), MakeBar(199m, 199.1m, 195m, 199.1m)).ToArray(),
        IndicatorType.CandleGravestoneDoji
            => BuildContextAndAppend(1, MakeBar(203m, 207.1m, 203m, 203.1m), MakeBar(203m, 207.1m, 203m, 203.1m)).ToArray(),
        // ---- V2-9: 16 three-bar convergence tails (40 + 3 = 43 bars)
        IndicatorType.CandleMorningStar
            => BuildContextAndAppend(-1, MakeBar(200m, 200.2m, 195.8m, 196m), MakeBar(195.2m, 196.2m, 194.2m, 195.6m), MakeBar(196m, 199.1m, 195.9m, 199m, 1200L)).ToArray(),
        IndicatorType.CandleEveningStar
            => BuildContextAndAppend(1, MakeBar(200m, 204.2m, 199.8m, 204m), MakeBar(204.4m, 205.4m, 203.4m, 204.8m), MakeBar(204m, 204.1m, 200.9m, 201m, 1200L)).ToArray(),
        IndicatorType.CandleThreeWhiteSoldiers
            => BuildContextAndAppend(-1, MakeBar(198m, 200.2m, 197.8m, 200m), MakeBar(199m, 201.7m, 198.8m, 201.5m), MakeBar(200m, 203.2m, 199.8m, 203m)).ToArray(),
        IndicatorType.CandleThreeBlackCrows
            => BuildContextAndAppend(1, MakeBar(202m, 202.2m, 199.8m, 200m), MakeBar(201m, 201.2m, 198.3m, 198.5m), MakeBar(200m, 200.2m, 196.8m, 197m)).ToArray(),
        IndicatorType.CandleBullishSideBySideWhiteLines
            => BuildContextAndAppend(1, MakeBar(199m, 201.2m, 198.8m, 201m), MakeBar(201.5m, 203.7m, 201.1m, 203.5m), MakeBar(201.8m, 204.2m, 201.6m, 204m)).ToArray(),
        IndicatorType.CandleBearishSideBySideWhiteLines
            => BuildContextAndAppend(-1, MakeBar(201m, 201.2m, 198.8m, 199m), MakeBar(196.5m, 198.7m, 196.3m, 198.5m), MakeBar(196.8m, 198.9m, 196.6m, 198.9m)).ToArray(),
        IndicatorType.CandleBullishAbandonedBaby
            => BuildContextAndAppend(-1, MakeBar(200m, 200.2m, 196.8m, 197m), MakeBar(195.5m, 196.5m, 194.5m, 195.5m), MakeBar(197m, 200.2m, 196.8m, 200m, 1250L)).ToArray(),
        IndicatorType.CandleBearishAbandonedBaby
            => BuildContextAndAppend(1, MakeBar(200m, 203.2m, 199.8m, 203m), MakeBar(204.5m, 205.5m, 203.5m, 204.5m), MakeBar(203m, 203.2m, 199.8m, 200m)).ToArray(),
        IndicatorType.CandleAdvanceBlock
            => BuildContextAndAppend(1, MakeBar(198m, 202.2m, 197.8m, 202m), MakeBar(200m, 203.8m, 199.8m, 203m), MakeBar(202m, 205.2m, 201.8m, 204m)).ToArray(),
        IndicatorType.CandleDeliberation
            => BuildContextAndAppend(1, MakeBar(199m, 201.2m, 198.8m, 201m), MakeBar(200m, 202.7m, 199.8m, 202.5m), MakeBar(202.2m, 203.2m, 202.0m, 203.0m)).ToArray(),
        IndicatorType.CandleStickSandwich
            => BuildContextAndAppend(-1, MakeBar(200m, 200.2m, 196.8m, 197m), MakeBar(197.5m, 200.7m, 197.3m, 200.5m), MakeBar(200.3m, 200.5m, 196.9m, 197.2m)).ToArray(),
        IndicatorType.CandleBullishTasukiGap
            => BuildContextAndAppend(1, MakeBar(199m, 201.2m, 198.8m, 201m), MakeBar(201.5m, 203.7m, 201.3m, 203.5m), MakeBar(203.5m, 203.7m, 201.1m, 201.2m)).ToArray(),
        IndicatorType.CandleBearishTasukiGap
            => BuildContextAndAppend(-1, MakeBar(201m, 201.2m, 198.8m, 199m), MakeBar(198.5m, 198.7m, 196.3m, 196.5m), MakeBar(196.5m, 198.9m, 196.3m, 198.8m)).ToArray(),
        IndicatorType.CandleBullishGapThreeMethods
            => BuildContextAndAppend(1, MakeBar(199m, 201.2m, 198.8m, 201m), MakeBar(201.5m, 203.7m, 201.3m, 203.5m), MakeBar(203.5m, 203.7m, 199.8m, 200m)).ToArray(),
        IndicatorType.CandleBearishGapThreeMethods
            => BuildContextAndAppend(-1, MakeBar(201m, 201.2m, 198.8m, 199m), MakeBar(198.5m, 198.7m, 196.3m, 196.5m), MakeBar(196.5m, 200.2m, 196.3m, 200m)).ToArray(),
        IndicatorType.CandleIdenticalThreeCrows
            => BuildContextAndAppend(1, MakeBar(202m, 202.2m, 199.7m, 200m), MakeBar(200.39m, 200.6m, 197.7m, 198m), MakeBar(198.35m, 198.6m, 195.8m, 196m)).ToArray(),
        // ---- V2-10: 9 four/five-bar convergence tails (44 or 45 bars)
        IndicatorType.CandleBullishThreeLineStrike
            => BuildContextAndAppend(1, MakeBar(199m, 200.7m, 198.8m, 200.5m), MakeBar(199.8m, 201.7m, 199.6m, 201.5m), MakeBar(200.8m, 202.7m, 200.6m, 202.5m), MakeBar(202.3m, 202.6m, 198.3m, 198.5m)).ToArray(),
        IndicatorType.CandleBearishThreeLineStrike
            => BuildContextAndAppend(-1, MakeBar(201m, 201.2m, 199.3m, 199.5m), MakeBar(200.2m, 200.4m, 198.3m, 198.5m), MakeBar(199.2m, 199.4m, 197.3m, 197.5m), MakeBar(197.7m, 201.7m, 197.4m, 201.5m)).ToArray(),
        IndicatorType.CandleConcealingBabySwallow
            => BuildContextAndAppend(-1, MakeBar(204m, 204.1m, 199.9m, 200m), MakeBar(203m, 203.1m, 198.9m, 199m), MakeBar(198.5m, 199.6m, 197.4m, 197.5m), MakeBar(200m, 200.2m, 196.8m, 197m)).ToArray(),
        IndicatorType.CandleRisingThreeMethods
            => BuildContextAndAppend(1, MakeBar(199m, 203.2m, 198.8m, 203m), MakeBar(202.5m, 203.0m, 201.5m, 202.0m), MakeBar(202.0m, 202.5m, 201.0m, 201.5m), MakeBar(201.5m, 202.0m, 200.5m, 201.0m), MakeBar(201.0m, 204.2m, 200.8m, 204.0m)).ToArray(),
        IndicatorType.CandleFallingThreeMethods
            => BuildContextAndAppend(-1, MakeBar(201m, 201.2m, 196.8m, 197m), MakeBar(197.5m, 198.5m, 197.0m, 198.0m), MakeBar(198.0m, 199.0m, 197.5m, 198.5m), MakeBar(198.5m, 199.5m, 198.0m, 199.0m), MakeBar(199.0m, 199.2m, 195.8m, 196.0m)).ToArray(),
        IndicatorType.CandleMatHold
            => BuildContextAndAppend(1, MakeBar(198m, 202.2m, 197.8m, 202m), MakeBar(202.3m, 203.4m, 201.8m, 202.6m), MakeBar(202.4m, 202.8m, 201.0m, 201.8m), MakeBar(201.7m, 202.2m, 200.5m, 201.1m), MakeBar(201.2m, 204.4m, 201.0m, 204.2m)).ToArray(),
        IndicatorType.CandleLadderBottom
            => BuildContextAndAppend(-1, MakeBar(201m, 201.2m, 199.2m, 199.5m), MakeBar(200.2m, 200.4m, 198.2m, 198.5m), MakeBar(199.2m, 199.4m, 197.2m, 197.5m), MakeBar(197.5m, 198.5m, 196.8m, 197.0m), MakeBar(197.8m, 200.2m, 197.6m, 200.0m)).ToArray(),
        IndicatorType.CandleBullishBreakaway
            => BuildContextAndAppend(-1, MakeBar(200m, 200.2m, 195.8m, 196m), MakeBar(195.7m, 195.9m, 194.3m, 194.5m), MakeBar(194.6m, 195.2m, 193.6m, 194.2m), MakeBar(194.2m, 194.8m, 193.2m, 193.8m), MakeBar(193.5m, 196.1m, 193.3m, 195.95m)).ToArray(),
        IndicatorType.CandleBearishBreakaway
            => BuildContextAndAppend(1, MakeBar(200m, 204.2m, 199.8m, 204m), MakeBar(204.3m, 205.7m, 204.1m, 205.5m), MakeBar(205.4m, 206.4m, 204.8m, 205.8m), MakeBar(205.8m, 206.8m, 205.2m, 206.2m), MakeBar(206.5m, 206.7m, 203.9m, 204.05m)).ToArray(),
        _ => System.Array.Empty<CandleData>(),
    };
}
