using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Candles;

/// <summary>Meaning of a candlestick pattern for the colour of its flag only (the flag itself has no direction series: the type name carries it).</summary>
public enum CandleBias
{
    Bullish,
    Bearish,
    Neutral,
}

/// <summary>
/// One candlestick flag: the indicator type, the <see cref="CandlePatternType"/> it flags, the number of bars the pattern spans (the first bar that can hold a value is that number),
/// its colour group, and whether it is one of the four umbrella patterns that can require the prior trend.
/// </summary>
public sealed record CandlePatternFlagEntry(IndicatorType Type, CandlePatternType Pattern, int Length, CandleBias Bias, bool IsUmbrella = false);

/// <summary>
/// The single definition of the 43 candlestick flag indicators (plan ChartPatterns56, section 5.1). The catalog rows (<see cref="PatternFlagIndicatorCatalog"/>), the indicator base and the tests all read this table.
/// </summary>
public static class CandlePatternFlagTable
{
    private static readonly CandlePatternFlagEntry[] EntryList =
    {
        // 1 bar
        new(IndicatorType.CandleBullishMarubozu, CandlePatternType.BullishMarubozu, 1, CandleBias.Bullish),
        new(IndicatorType.CandleBearishMarubozu, CandlePatternType.BearishMarubozu, 1, CandleBias.Bearish),
        new(IndicatorType.CandleHammer, CandlePatternType.BullishUmbrella, 1, CandleBias.Bullish, IsUmbrella: true),
        new(IndicatorType.CandleInvertedHammer, CandlePatternType.BullishInvertedUmbrella, 1, CandleBias.Bullish, IsUmbrella: true),
        new(IndicatorType.CandleHangingMan, CandlePatternType.BearishUmbrella, 1, CandleBias.Bearish, IsUmbrella: true),
        new(IndicatorType.CandleShootingStar, CandlePatternType.BearishInvertedUmbrella, 1, CandleBias.Bearish, IsUmbrella: true),
        new(IndicatorType.CandleCrossDoji, CandlePatternType.CrossDoji, 1, CandleBias.Neutral),
        new(IndicatorType.CandleDragonflyDoji, CandlePatternType.DragonflyDoji, 1, CandleBias.Neutral),
        new(IndicatorType.CandleGravestoneDoji, CandlePatternType.GravestoneDoji, 1, CandleBias.Neutral),

        // 2 bars
        new(IndicatorType.CandleBullishEngulfing, CandlePatternType.BullishEngulfing, 2, CandleBias.Bullish),
        new(IndicatorType.CandleBearishEngulfing, CandlePatternType.BearishEngulfing, 2, CandleBias.Bearish),
        new(IndicatorType.CandleBullishHarami, CandlePatternType.BullishHarami, 2, CandleBias.Bullish),
        new(IndicatorType.CandleBearishHarami, CandlePatternType.BearishHarami, 2, CandleBias.Bearish),
        new(IndicatorType.CandlePiercingLine, CandlePatternType.PiercingLine, 2, CandleBias.Bullish),
        new(IndicatorType.CandleDarkCloudCover, CandlePatternType.DarkCloudCover, 2, CandleBias.Bearish),
        new(IndicatorType.CandleHomingPigeon, CandlePatternType.HomingPigeon, 2, CandleBias.Bullish),
        new(IndicatorType.CandleBullishKicking, CandlePatternType.BullishKicking, 2, CandleBias.Bullish),
        new(IndicatorType.CandleBearishKicking, CandlePatternType.BearishKicking, 2, CandleBias.Bearish),

        // 3 bars
        new(IndicatorType.CandleMorningStar, CandlePatternType.MorningStar, 3, CandleBias.Bullish),
        new(IndicatorType.CandleEveningStar, CandlePatternType.EveningStar, 3, CandleBias.Bearish),
        new(IndicatorType.CandleThreeWhiteSoldiers, CandlePatternType.ThreeWhiteSoldiers, 3, CandleBias.Bullish),
        new(IndicatorType.CandleThreeBlackCrows, CandlePatternType.ThreeBlackCrows, 3, CandleBias.Bearish),
        new(IndicatorType.CandleBullishSideBySideWhiteLines, CandlePatternType.BullishSideBySideWhiteLines, 3, CandleBias.Bullish),
        new(IndicatorType.CandleBearishSideBySideWhiteLines, CandlePatternType.BearishSideBySideWhiteLines, 3, CandleBias.Bearish),
        new(IndicatorType.CandleBullishAbandonedBaby, CandlePatternType.BullishAbandonedBaby, 3, CandleBias.Bullish),
        new(IndicatorType.CandleBearishAbandonedBaby, CandlePatternType.BearishAbandonedBaby, 3, CandleBias.Bearish),
        new(IndicatorType.CandleAdvanceBlock, CandlePatternType.AdvanceBlock, 3, CandleBias.Bearish),
        new(IndicatorType.CandleDeliberation, CandlePatternType.Deliberation, 3, CandleBias.Bearish),
        new(IndicatorType.CandleStickSandwich, CandlePatternType.StickSandwich, 3, CandleBias.Bullish),
        new(IndicatorType.CandleBullishTasukiGap, CandlePatternType.BullishTasukiGap, 3, CandleBias.Bullish),
        new(IndicatorType.CandleBearishTasukiGap, CandlePatternType.BearishTasukiGap, 3, CandleBias.Bearish),
        new(IndicatorType.CandleBullishGapThreeMethods, CandlePatternType.BullishGapThreeMethods, 3, CandleBias.Bullish),
        new(IndicatorType.CandleBearishGapThreeMethods, CandlePatternType.BearishGapThreeMethods, 3, CandleBias.Bearish),
        new(IndicatorType.CandleIdenticalThreeCrows, CandlePatternType.IdenticalThreeCrows, 3, CandleBias.Bearish),

        // 4 bars
        new(IndicatorType.CandleBullishThreeLineStrike, CandlePatternType.BullishThreeLineStrike, 4, CandleBias.Bullish),
        new(IndicatorType.CandleBearishThreeLineStrike, CandlePatternType.BearishThreeLineStrike, 4, CandleBias.Bearish),
        new(IndicatorType.CandleConcealingBabySwallow, CandlePatternType.ConcealingBabySwallow, 4, CandleBias.Bullish),

        // 5 bars
        new(IndicatorType.CandleRisingThreeMethods, CandlePatternType.RisingThreeMethods, 5, CandleBias.Bullish),
        new(IndicatorType.CandleFallingThreeMethods, CandlePatternType.FallingThreeMethods, 5, CandleBias.Bearish),
        new(IndicatorType.CandleMatHold, CandlePatternType.MatHold, 5, CandleBias.Bullish),
        new(IndicatorType.CandleLadderBottom, CandlePatternType.LadderBottom, 5, CandleBias.Bullish),
        new(IndicatorType.CandleBullishBreakaway, CandlePatternType.BullishBreakaway, 5, CandleBias.Bullish),
        new(IndicatorType.CandleBearishBreakaway, CandlePatternType.BearishBreakaway, 5, CandleBias.Bearish),
    };

    private static readonly FrozenDictionary<IndicatorType, CandlePatternFlagEntry> ByType = BuildIndex();

    /// <summary>The 43 entries in definition order.</summary>
    public static IReadOnlyList<CandlePatternFlagEntry> Entries => EntryList;

    /// <summary>The entry of <paramref name="type"/>; throws when the type is not a candlestick flag.</summary>
    public static CandlePatternFlagEntry Get(IndicatorType type)
        => ByType.TryGetValue(type, out CandlePatternFlagEntry? entry)
            ? entry
            : throw new ArgumentException($"{type} is not a candlestick pattern flag.", nameof(type));

    /// <summary>The catalog rows of the candlestick flags (the table is the source; the catalog only lists them).</summary>
    internal static PatternFlagSpec[] CreateSpecs()
        => EntryList.Select(e =>
            {
                CoreCandlePatternParameter sample = CoreCandlePatternParameter.CreateFor(e.Type);
                return new PatternFlagSpec(
                    e.Type,
                    CoreIndicatorCategory.CandlestickPatterns,
                    sample.ComputeMinBars(e.Length),
                    PatternFlagEvaluationModel.FullSeries,
                    HasDirection: false,
                    ResolveColor(e.Bias),
                    sample.GetType(),
                    ParameterFactory: () => CoreCandlePatternParameter.CreateFor(e.Type));
            })
            .ToArray();

    private static IndicatorColor ResolveColor(CandleBias bias) => bias switch
    {
        CandleBias.Bullish => IndicatorDefaultConstants.PatternFlagBullishColor,
        CandleBias.Bearish => IndicatorDefaultConstants.PatternFlagBearishColor,
        _ => IndicatorDefaultConstants.PatternFlagNeutralColor,
    };

    private static FrozenDictionary<IndicatorType, CandlePatternFlagEntry> BuildIndex()
    {
        var index = new Dictionary<IndicatorType, CandlePatternFlagEntry>(EntryList.Length);
        foreach (CandlePatternFlagEntry entry in EntryList)
        {
            if (!index.TryAdd(entry.Type, entry))
            {
                throw new InvalidOperationException($"The candlestick flag table lists '{entry.Type}' twice.");
            }
        }

        return index.ToFrozenDictionary();
    }
}
