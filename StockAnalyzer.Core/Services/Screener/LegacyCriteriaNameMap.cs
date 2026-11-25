using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services.Screener;

/// <summary>
/// Explicit 56-entry bijection between legacy <c>Screener / Criteria</c> short names and v2 <see cref="IndicatorType"/> flags
/// (integration spec §3.2, plan ChartPatterns56 v2 run V2-11; ledger <c>sa_pattern_literal_ledger_V2-11_ScreenerIntegration.md</c>).
/// Decoupled from <c>[Description]</c> attributes and <c>IndicatorType.TryParse</c>.
/// </summary>
public static class LegacyCriteriaNameMap
{
    private static readonly (string CriteriaName, IndicatorType IndicatorType)[] Entries =
    {
        // Pattern Recognition (3)
        ("Head and Shoulders", IndicatorType.PatternHeadAndShoulders),
        ("Double Top / Bottom", IndicatorType.PatternDoubleTopBottom),
        ("Bullish / Bearish Flag", IndicatorType.PatternFlag),

        // Market Structure (3)
        ("Break of Structure (BOS)", IndicatorType.MarketStructureBos),
        ("Change of Character (CHoCH)", IndicatorType.MarketStructureChoch),
        ("Higher High / Higher Low", IndicatorType.MarketStructureHhHl),

        // Harmonic (3)
        ("Gartley Pattern", IndicatorType.HarmonicGartley),
        ("Butterfly Pattern", IndicatorType.HarmonicButterfly),
        ("Bat Pattern", IndicatorType.HarmonicBat),

        // Trading Rules (2)
        ("Granville's Law Buy Signal", IndicatorType.GranvilleBuySignal),
        ("Granville's Law Sell Signal", IndicatorType.GranvilleSellSignal),

        // Wave Theory (2)
        ("Elliott Wave Impulse 1-5", IndicatorType.ElliottWaveImpulse),
        ("Elliott Wave Corrective ABC", IndicatorType.ElliottWaveCorrective),

        // Candlestick Patterns (43)
        ("Bullish Marubozu", IndicatorType.CandleBullishMarubozu),
        ("Bearish Marubozu", IndicatorType.CandleBearishMarubozu),
        ("Bullish Engulfing", IndicatorType.CandleBullishEngulfing),
        ("Bearish Engulfing", IndicatorType.CandleBearishEngulfing),
        ("Bullish Harami", IndicatorType.CandleBullishHarami),
        ("Bearish Harami", IndicatorType.CandleBearishHarami),
        ("Morning Star", IndicatorType.CandleMorningStar),
        ("Evening Star", IndicatorType.CandleEveningStar),
        ("Hammer / Bullish Umbrella", IndicatorType.CandleHammer),
        ("Inverted Hammer", IndicatorType.CandleInvertedHammer),
        ("Hanging Man / Bearish Umbrella", IndicatorType.CandleHangingMan),
        ("Shooting Star", IndicatorType.CandleShootingStar),
        ("Doji / Cross Doji", IndicatorType.CandleCrossDoji),
        ("Dragonfly Doji", IndicatorType.CandleDragonflyDoji),
        ("Gravestone Doji", IndicatorType.CandleGravestoneDoji),
        ("Piercing Line", IndicatorType.CandlePiercingLine),
        ("Dark Cloud Cover", IndicatorType.CandleDarkCloudCover),
        ("Three White Soldiers", IndicatorType.CandleThreeWhiteSoldiers),
        ("Three Black Crows", IndicatorType.CandleThreeBlackCrows),
        ("Rising Three Methods", IndicatorType.CandleRisingThreeMethods),
        ("Falling Three Methods", IndicatorType.CandleFallingThreeMethods),
        ("Mat Hold", IndicatorType.CandleMatHold),
        ("Bullish Side-by-Side White Lines", IndicatorType.CandleBullishSideBySideWhiteLines),
        ("Bearish Side-by-Side White Lines", IndicatorType.CandleBearishSideBySideWhiteLines),
        ("Bullish Three-Line Strike", IndicatorType.CandleBullishThreeLineStrike),
        ("Bearish Three-Line Strike", IndicatorType.CandleBearishThreeLineStrike),
        ("Bullish Abandoned Baby", IndicatorType.CandleBullishAbandonedBaby),
        ("Bearish Abandoned Baby", IndicatorType.CandleBearishAbandonedBaby),
        ("Advance Block", IndicatorType.CandleAdvanceBlock),
        ("Deliberation", IndicatorType.CandleDeliberation),
        ("Stick Sandwich", IndicatorType.CandleStickSandwich),
        ("Ladder Bottom", IndicatorType.CandleLadderBottom),
        ("Homing Pigeon", IndicatorType.CandleHomingPigeon),
        ("Bullish Tasuki Gap", IndicatorType.CandleBullishTasukiGap),
        ("Bearish Tasuki Gap", IndicatorType.CandleBearishTasukiGap),
        ("Bullish Gap Three Methods", IndicatorType.CandleBullishGapThreeMethods),
        ("Bearish Gap Three Methods", IndicatorType.CandleBearishGapThreeMethods),
        ("Bullish Breakaway", IndicatorType.CandleBullishBreakaway),
        ("Bearish Breakaway", IndicatorType.CandleBearishBreakaway),
        ("Bullish Kicking", IndicatorType.CandleBullishKicking),
        ("Bearish Kicking", IndicatorType.CandleBearishKicking),
        ("Concealing Baby Swallow", IndicatorType.CandleConcealingBabySwallow),
        ("Identical Three Crows", IndicatorType.CandleIdenticalThreeCrows),
    };

    private static readonly FrozenDictionary<string, IndicatorType> CriteriaToIndicator = BuildCriteriaMap();
    private static readonly FrozenDictionary<IndicatorType, string> IndicatorToCriteria = BuildIndicatorMap();

    /// <summary>Read-only lookup from legacy Screener Criteria ShortName (OrdinalIgnoreCase) to replacement <see cref="IndicatorType"/>.</summary>
    public static IReadOnlyDictionary<string, IndicatorType> ByCriteriaName => CriteriaToIndicator;

    /// <summary>Reverse lookup from pattern flag <see cref="IndicatorType"/> to its legacy Criteria ShortName.</summary>
    public static IReadOnlyDictionary<IndicatorType, string> ByIndicatorType => IndicatorToCriteria;

    /// <summary>Attempts to resolve a legacy Criteria short name to its replacement pattern-flag <see cref="IndicatorType"/>.</summary>
    public static bool TryResolve(string? criteriaName, out IndicatorType indicatorType)
    {
        if (string.IsNullOrWhiteSpace(criteriaName))
        {
            indicatorType = default;
            return false;
        }

        return CriteriaToIndicator.TryGetValue(criteriaName.Trim(), out indicatorType);
    }

    /// <summary>Attempts to obtain the legacy Criteria short name corresponding to <paramref name="indicatorType"/>.</summary>
    public static bool TryGetCriteriaName(IndicatorType indicatorType, out string criteriaName)
        => IndicatorToCriteria.TryGetValue(indicatorType, out criteriaName!);

    private static FrozenDictionary<string, IndicatorType> BuildCriteriaMap()
    {
        var map = new Dictionary<string, IndicatorType>(Entries.Length, StringComparer.OrdinalIgnoreCase);
        foreach ((string name, IndicatorType type) in Entries)
        {
            if (!map.TryAdd(name, type))
            {
                throw new InvalidOperationException($"Duplicate legacy criteria mapping '{name}'.");
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<IndicatorType, string> BuildIndicatorMap()
    {
        var map = new Dictionary<IndicatorType, string>(Entries.Length);
        foreach ((string name, IndicatorType type) in Entries)
        {
            if (!map.TryAdd(type, name))
            {
                throw new InvalidOperationException($"Duplicate indicator type in LegacyCriteriaNameMap: '{type}'.");
            }
        }

        return map.ToFrozenDictionary();
    }
}
