using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests.Indicators.PatternFlag;

/// <summary>
/// Fixed bar windows for the candlestick flag tests (plan ChartPatterns56, probes P-7 and P-8). Every window is 20 context bars from <see cref="ContextBars"/> followed by the listed bars
/// (open, high, low, close each). <see cref="Exact"/>: the legacy single-answer <c>CandlePatternDetector.DetectPattern</c> returns exactly the pattern of the key. <see cref="Shadowed"/>: the legacy
/// single answer is a DIFFERENT (longer-precedence) pattern while <c>DetectPatterns</c> also lists the key pattern. The windows were found by a seeded random search (seed 20261125) over
/// bars on a 0.25 tick grid; ConcealingBabySwallow was not reached by the search and is built by hand from the rules of the detector.
/// </summary>
internal static class CandlePatternFixtures
{
    public const int ContextBars = 20;

    public static readonly IReadOnlyDictionary<CandlePatternType, decimal[][]> Exact = new Dictionary<CandlePatternType, decimal[][]>
    {
        [CandlePatternType.BullishMarubozu] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 103.50m, 95.50m, 97.50m }, new[] { 97.50m, 98.75m, 97.00m, 98.50m }, new[] { 98.50m, 98.75m, 93.50m, 96.50m }, new[] { 95.75m, 97.75m, 95.75m, 97.75m } },
        [CandlePatternType.BearishMarubozu] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 100.00m, 102.00m, 99.25m, 99.75m }, new[] { 99.75m, 101.75m, 99.75m, 99.75m }, new[] { 99.75m, 99.75m, 97.75m, 97.75m } },
        [CandlePatternType.BullishUmbrella] = new[] { new[] { 100.00m, 100.00m, 99.50m, 100.00m }, new[] { 100.50m, 100.50m, 98.25m, 98.50m }, new[] { 98.50m, 98.50m, 93.50m, 95.50m }, new[] { 95.50m, 95.50m, 94.50m, 95.00m }, new[] { 95.00m, 95.25m, 93.00m, 95.25m } },
        [CandlePatternType.BullishInvertedUmbrella] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.25m, 100.75m, 99.25m, 99.25m }, new[] { 100.75m, 100.75m, 94.75m, 97.75m }, new[] { 97.75m, 100.75m, 97.25m, 97.25m }, new[] { 97.25m, 99.50m, 97.25m, 97.50m } },
        [CandlePatternType.BearishUmbrella] = new[] { new[] { 100.00m, 103.00m, 99.25m, 99.75m }, new[] { 99.75m, 101.75m, 96.75m, 98.75m }, new[] { 98.75m, 101.75m, 97.75m, 97.75m }, new[] { 97.75m, 99.75m, 94.75m, 99.75m }, new[] { 100.25m, 100.25m, 96.75m, 99.75m } },
        [CandlePatternType.BearishInvertedUmbrella] = new[] { new[] { 101.50m, 101.50m, 101.00m, 101.25m }, new[] { 101.25m, 101.25m, 98.25m, 98.25m }, new[] { 98.25m, 101.25m, 96.25m, 101.25m }, new[] { 101.25m, 104.25m, 98.75m, 100.75m }, new[] { 100.75m, 103.75m, 100.50m, 100.50m } },
        [CandlePatternType.CrossDoji] = new[] { new[] { 100.00m, 100.75m, 97.00m, 100.50m }, new[] { 100.50m, 105.50m, 98.50m, 103.50m }, new[] { 103.50m, 104.00m, 103.50m, 103.50m }, new[] { 103.50m, 105.50m, 102.50m, 102.50m }, new[] { 103.50m, 106.75m, 100.50m, 103.75m } },
        [CandlePatternType.DragonflyDoji] = new[] { new[] { 100.00m, 106.00m, 99.75m, 103.00m }, new[] { 103.00m, 105.00m, 99.00m, 102.00m }, new[] { 102.00m, 102.25m, 101.50m, 102.00m }, new[] { 101.00m, 103.00m, 98.00m, 101.00m }, new[] { 101.75m, 101.75m, 98.75m, 101.75m } },
        [CandlePatternType.GravestoneDoji] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 100.25m, 99.25m, 100.00m }, new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 99.75m, 98.75m, 99.00m }, new[] { 99.00m, 99.25m, 99.00m, 99.00m } },
        [CandlePatternType.BullishEngulfing] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.25m, 100.25m, 101.00m }, new[] { 101.00m, 101.25m, 100.25m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 100.00m, 103.50m, 100.00m, 103.00m } },
        [CandlePatternType.BearishEngulfing] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.50m, 98.50m, 101.50m }, new[] { 101.50m, 101.75m, 99.50m, 101.75m }, new[] { 101.50m, 103.75m, 101.50m, 101.75m }, new[] { 101.75m, 101.75m, 100.75m, 101.25m } },
        [CandlePatternType.BullishHarami] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 100.00m, 103.00m, 100.00m, 103.00m }, new[] { 103.00m, 103.00m, 99.50m, 100.00m }, new[] { 100.00m, 100.50m, 100.00m, 100.25m } },
        [CandlePatternType.BearishHarami] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 103.50m, 100.25m, 100.25m }, new[] { 98.75m, 101.75m, 98.25m, 98.25m }, new[] { 98.25m, 100.50m, 98.25m, 100.25m }, new[] { 100.25m, 100.50m, 97.00m, 100.00m } },
        [CandlePatternType.PiercingLine] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 102.50m, 97.50m, 97.50m }, new[] { 96.00m, 101.00m, 96.00m, 99.00m } },
        [CandlePatternType.DarkCloudCover] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.25m, 100.25m, 101.00m }, new[] { 101.00m, 101.25m, 101.00m, 101.25m }, new[] { 101.25m, 103.50m, 101.25m, 103.25m }, new[] { 104.00m, 104.00m, 99.00m, 102.00m } },
        [CandlePatternType.HomingPigeon] = new[] { new[] { 100.00m, 102.00m, 97.00m, 102.00m }, new[] { 102.00m, 102.50m, 101.75m, 102.50m }, new[] { 102.50m, 104.50m, 96.50m, 99.50m }, new[] { 99.00m, 99.25m, 96.50m, 97.00m }, new[] { 98.25m, 101.25m, 97.50m, 97.75m } },
        [CandlePatternType.BullishKicking] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 99.75m, 98.75m, 99.00m }, new[] { 99.00m, 99.50m, 99.00m, 99.00m }, new[] { 99.00m, 99.00m, 98.00m, 98.00m }, new[] { 99.75m, 102.75m, 99.75m, 102.75m } },
        [CandlePatternType.BearishKicking] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.25m, 100.25m, 101.00m }, new[] { 101.00m, 101.75m, 100.75m, 101.50m }, new[] { 101.25m, 102.25m, 101.25m, 102.25m }, new[] { 100.50m, 100.50m, 97.50m, 97.50m } },
        [CandlePatternType.MorningStar] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.25m, 100.25m, 101.00m }, new[] { 101.25m, 101.75m, 98.00m, 98.25m }, new[] { 98.00m, 100.00m, 97.25m, 97.75m }, new[] { 97.75m, 100.75m, 97.75m, 100.75m } },
        [CandlePatternType.EveningStar] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 100.25m, 99.25m, 100.00m }, new[] { 100.75m, 104.75m, 98.75m, 102.75m }, new[] { 103.50m, 103.75m, 101.25m, 103.25m }, new[] { 103.25m, 103.25m, 98.25m, 100.25m } },
        [CandlePatternType.ThreeWhiteSoldiers] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 99.75m, 98.75m, 99.00m }, new[] { 99.00m, 99.75m, 98.75m, 99.50m }, new[] { 99.50m, 100.25m, 99.25m, 100.00m }, new[] { 100.00m, 103.25m, 99.75m, 103.00m } },
        [CandlePatternType.ThreeBlackCrows] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.25m, 100.25m, 101.00m }, new[] { 101.00m, 101.25m, 100.25m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 100.00m, 100.00m, 99.25m, 99.75m } },
        [CandlePatternType.BullishSideBySideWhiteLines] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 102.25m, 105.75m, 102.25m, 102.75m }, new[] { 102.25m, 103.00m, 102.00m, 102.75m } },
        [CandlePatternType.BearishSideBySideWhiteLines] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 100.25m, 99.25m, 100.00m }, new[] { 99.75m, 99.75m, 97.75m, 97.75m }, new[] { 97.25m, 97.50m, 96.75m, 97.50m }, new[] { 97.25m, 97.50m, 97.25m, 97.50m } },
        [CandlePatternType.BullishAbandonedBaby] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.25m, 100.50m, 97.00m, 100.00m }, new[] { 100.00m, 100.00m, 97.50m, 98.00m }, new[] { 97.00m, 97.00m, 97.00m, 97.00m }, new[] { 99.00m, 101.50m, 99.00m, 101.00m } },
        [CandlePatternType.BearishAbandonedBaby] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 101.00m, 102.00m, 100.50m, 102.00m }, new[] { 104.00m, 104.25m, 103.50m, 104.00m }, new[] { 102.00m, 102.50m, 99.00m, 99.00m } },
        [CandlePatternType.AdvanceBlock] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 100.25m, 99.25m, 100.00m }, new[] { 98.50m, 100.75m, 98.50m, 100.50m }, new[] { 100.50m, 101.50m, 100.25m, 101.50m }, new[] { 101.50m, 105.00m, 101.00m, 102.00m } },
        [CandlePatternType.Deliberation] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.25m, 100.25m, 101.00m }, new[] { 102.00m, 104.00m, 99.00m, 102.00m } },
        [CandlePatternType.StickSandwich] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 100.00m, 100.50m, 97.00m, 97.00m }, new[] { 97.25m, 97.75m, 96.75m, 97.50m }, new[] { 97.50m, 97.50m, 94.00m, 97.00m } },
        [CandlePatternType.BullishTasukiGap] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 99.75m, 99.50m, 99.50m }, new[] { 99.50m, 101.50m, 97.50m, 101.50m }, new[] { 103.00m, 103.50m, 103.00m, 103.50m }, new[] { 103.50m, 106.50m, 100.50m, 102.50m } },
        [CandlePatternType.BearishTasukiGap] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 102.25m, 102.50m, 98.75m, 101.75m }, new[] { 101.75m, 101.75m, 96.75m, 98.75m }, new[] { 98.00m, 100.00m, 93.00m, 96.00m }, new[] { 96.25m, 98.25m, 96.25m, 98.25m } },
        [CandlePatternType.BullishGapThreeMethods] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.25m, 100.25m, 101.00m }, new[] { 101.00m, 101.75m, 100.75m, 101.50m }, new[] { 101.75m, 102.00m, 101.75m, 102.00m }, new[] { 101.75m, 101.75m, 101.50m, 101.50m } },
        [CandlePatternType.BearishGapThreeMethods] = new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.50m, 97.50m, 101.00m }, new[] { 101.00m, 104.00m, 99.75m, 100.00m }, new[] { 98.75m, 98.75m, 98.50m, 98.50m }, new[] { 98.50m, 100.50m, 98.25m, 100.50m } },
        [CandlePatternType.IdenticalThreeCrows] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 100.00m, 99.00m, 100.00m }, new[] { 100.00m, 100.25m, 97.00m, 97.00m }, new[] { 97.00m, 97.00m, 96.00m, 96.00m }, new[] { 96.00m, 96.25m, 94.00m, 94.00m } },
        [CandlePatternType.BullishThreeLineStrike] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 100.25m, 99.25m, 100.00m }, new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.25m, 100.25m, 101.00m }, new[] { 101.00m, 101.00m, 97.50m, 98.00m } },
        [CandlePatternType.BearishThreeLineStrike] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 99.75m, 98.75m, 99.00m }, new[] { 99.00m, 99.25m, 98.25m, 98.50m }, new[] { 98.50m, 98.75m, 97.75m, 98.00m }, new[] { 98.00m, 102.00m, 97.75m, 100.00m } },
        [CandlePatternType.ConcealingBabySwallow] = new[] { new[] { 110.00m, 110.00m, 106.00m, 106.00m }, new[] { 106.00m, 106.00m, 102.00m, 102.00m }, new[] { 100.00m, 102.50m, 98.50m, 99.00m }, new[] { 103.50m, 103.50m, 97.50m, 97.50m } },
        [CandlePatternType.RisingThreeMethods] = new[] { new[] { 102.00m, 105.50m, 102.00m, 105.00m }, new[] { 105.00m, 105.25m, 102.00m, 105.00m }, new[] { 105.00m, 105.00m, 104.00m, 104.50m }, new[] { 104.50m, 104.75m, 104.00m, 104.25m }, new[] { 106.00m, 109.50m, 106.00m, 109.00m } },
        [CandlePatternType.FallingThreeMethods] = new[] { new[] { 100.00m, 100.50m, 96.50m, 97.00m }, new[] { 97.00m, 97.25m, 96.75m, 97.25m }, new[] { 97.25m, 99.25m, 97.00m, 97.00m }, new[] { 98.00m, 98.00m, 98.00m, 98.00m }, new[] { 98.00m, 101.00m, 95.00m, 95.00m } },
        [CandlePatternType.MatHold] = new[] { new[] { 100.00m, 103.00m, 99.75m, 103.00m }, new[] { 105.00m, 105.50m, 104.50m, 105.00m }, new[] { 104.50m, 106.50m, 104.25m, 104.25m }, new[] { 104.25m, 104.75m, 103.75m, 104.50m }, new[] { 104.50m, 110.50m, 104.50m, 107.50m } },
        [CandlePatternType.LadderBottom] = new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 99.75m, 98.75m, 99.00m }, new[] { 99.00m, 99.00m, 96.50m, 98.50m }, new[] { 98.50m, 98.75m, 98.50m, 98.50m }, new[] { 99.75m, 100.25m, 96.75m, 100.25m } },
        [CandlePatternType.BullishBreakaway] = new[] { new[] { 98.75m, 98.75m, 96.75m, 96.75m }, new[] { 95.00m, 95.00m, 91.50m, 92.00m }, new[] { 93.00m, 95.00m, 92.50m, 92.50m }, new[] { 92.50m, 92.50m, 92.00m, 92.50m }, new[] { 92.50m, 98.50m, 90.50m, 95.50m } },
        [CandlePatternType.BearishBreakaway] = new[] { new[] { 100.00m, 103.00m, 99.50m, 103.00m }, new[] { 104.75m, 105.50m, 104.75m, 105.00m }, new[] { 105.00m, 105.50m, 105.00m, 105.25m }, new[] { 105.25m, 107.25m, 104.75m, 105.25m }, new[] { 105.25m, 105.25m, 101.25m, 103.25m } },
    };

    /// <summary>Windows where the pattern of the key is listed by DetectPatterns but the single answer DetectPattern is <c>LegacyAnswer</c>.</summary>
    public static readonly IReadOnlyDictionary<CandlePatternType, (CandlePatternType LegacyAnswer, decimal[][] Bars)> Shadowed = new Dictionary<CandlePatternType, (CandlePatternType, decimal[][])>
    {
        [CandlePatternType.BullishMarubozu] = (CandlePatternType.BullishEngulfing, new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 100.25m, 99.25m, 100.00m }, new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 100.00m, 101.00m, 100.00m, 101.00m } }),
        [CandlePatternType.CrossDoji] = (CandlePatternType.Deliberation, new[] { new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 100.75m, 99.75m, 100.00m }, new[] { 100.00m, 100.75m, 99.75m, 100.50m }, new[] { 100.50m, 101.25m, 100.25m, 101.00m }, new[] { 102.00m, 104.00m, 99.00m, 102.00m } }),
        [CandlePatternType.ThreeWhiteSoldiers] = (CandlePatternType.AdvanceBlock, new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 100.25m, 99.25m, 100.00m }, new[] { 98.50m, 100.75m, 98.50m, 100.50m }, new[] { 100.50m, 101.50m, 100.25m, 101.50m }, new[] { 101.50m, 105.00m, 101.00m, 102.00m } }),
        [CandlePatternType.Deliberation] = (CandlePatternType.AdvanceBlock, new[] { new[] { 100.00m, 100.25m, 99.25m, 99.50m }, new[] { 99.50m, 100.25m, 99.25m, 100.00m }, new[] { 100.00m, 103.00m, 98.00m, 103.00m }, new[] { 103.00m, 105.00m, 103.00m, 105.00m }, new[] { 105.00m, 107.50m, 104.50m, 105.50m } }),
    };

    /// <summary>The flat-ish context: a bar that goes up 0.5 and a bar that goes back down 0.5, starting at 100 (so the first listed bar opens at 100).</summary>
    public static List<CandleData> Build(decimal[][] lastBars)
    {
        var bars = new List<CandleData>(ContextBars + lastBars.Length);
        decimal price = 100m;
        for (int i = 0; i < ContextBars; i++)
        {
            decimal open = price;
            decimal close = open + (i % 2 == 0 ? 0.5m : -0.5m);
            bars.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(i), open, Math.Max(open, close) + 0.25m, Math.Min(open, close) - 0.25m, close, 1000));
            price = close;
        }

        for (int i = 0; i < lastBars.Length; i++)
        {
            decimal[] b = lastBars[i];
            bars.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(ContextBars + i), b[0], b[1], b[2], b[3], 1000));
        }

        return bars;
    }
}
