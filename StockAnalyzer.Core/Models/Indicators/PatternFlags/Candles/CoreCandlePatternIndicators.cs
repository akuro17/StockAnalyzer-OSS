namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Candles;

// The 43 candlestick flag indicators (plan ChartPatterns56, section 5.1). Each class only names its type: the pattern, the length, the colour and the parameter class come from
// CandlePatternFlagTable, the evaluation from CoreCandlePatternIndicatorBase. The IndicatorFactory finds a class by its attribute, so one class per type cannot be avoided.

[StockAnalyzerIndicator(IndicatorType.CandleBullishMarubozu)]
public sealed class CoreCandleBullishMarubozuIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBullishMarubozuIndicator() : base(IndicatorType.CandleBullishMarubozu)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBearishMarubozu)]
public sealed class CoreCandleBearishMarubozuIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBearishMarubozuIndicator() : base(IndicatorType.CandleBearishMarubozu)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleHammer)]
public sealed class CoreCandleHammerIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleHammerIndicator() : base(IndicatorType.CandleHammer)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleInvertedHammer)]
public sealed class CoreCandleInvertedHammerIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleInvertedHammerIndicator() : base(IndicatorType.CandleInvertedHammer)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleHangingMan)]
public sealed class CoreCandleHangingManIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleHangingManIndicator() : base(IndicatorType.CandleHangingMan)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleShootingStar)]
public sealed class CoreCandleShootingStarIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleShootingStarIndicator() : base(IndicatorType.CandleShootingStar)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleCrossDoji)]
public sealed class CoreCandleCrossDojiIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleCrossDojiIndicator() : base(IndicatorType.CandleCrossDoji)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleDragonflyDoji)]
public sealed class CoreCandleDragonflyDojiIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleDragonflyDojiIndicator() : base(IndicatorType.CandleDragonflyDoji)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleGravestoneDoji)]
public sealed class CoreCandleGravestoneDojiIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleGravestoneDojiIndicator() : base(IndicatorType.CandleGravestoneDoji)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBullishEngulfing)]
public sealed class CoreCandleBullishEngulfingIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBullishEngulfingIndicator() : base(IndicatorType.CandleBullishEngulfing)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBearishEngulfing)]
public sealed class CoreCandleBearishEngulfingIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBearishEngulfingIndicator() : base(IndicatorType.CandleBearishEngulfing)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBullishHarami)]
public sealed class CoreCandleBullishHaramiIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBullishHaramiIndicator() : base(IndicatorType.CandleBullishHarami)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBearishHarami)]
public sealed class CoreCandleBearishHaramiIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBearishHaramiIndicator() : base(IndicatorType.CandleBearishHarami)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandlePiercingLine)]
public sealed class CoreCandlePiercingLineIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandlePiercingLineIndicator() : base(IndicatorType.CandlePiercingLine)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleDarkCloudCover)]
public sealed class CoreCandleDarkCloudCoverIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleDarkCloudCoverIndicator() : base(IndicatorType.CandleDarkCloudCover)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleHomingPigeon)]
public sealed class CoreCandleHomingPigeonIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleHomingPigeonIndicator() : base(IndicatorType.CandleHomingPigeon)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBullishKicking)]
public sealed class CoreCandleBullishKickingIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBullishKickingIndicator() : base(IndicatorType.CandleBullishKicking)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBearishKicking)]
public sealed class CoreCandleBearishKickingIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBearishKickingIndicator() : base(IndicatorType.CandleBearishKicking)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleMorningStar)]
public sealed class CoreCandleMorningStarIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleMorningStarIndicator() : base(IndicatorType.CandleMorningStar)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleEveningStar)]
public sealed class CoreCandleEveningStarIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleEveningStarIndicator() : base(IndicatorType.CandleEveningStar)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleThreeWhiteSoldiers)]
public sealed class CoreCandleThreeWhiteSoldiersIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleThreeWhiteSoldiersIndicator() : base(IndicatorType.CandleThreeWhiteSoldiers)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleThreeBlackCrows)]
public sealed class CoreCandleThreeBlackCrowsIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleThreeBlackCrowsIndicator() : base(IndicatorType.CandleThreeBlackCrows)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBullishSideBySideWhiteLines)]
public sealed class CoreCandleBullishSideBySideWhiteLinesIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBullishSideBySideWhiteLinesIndicator() : base(IndicatorType.CandleBullishSideBySideWhiteLines)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBearishSideBySideWhiteLines)]
public sealed class CoreCandleBearishSideBySideWhiteLinesIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBearishSideBySideWhiteLinesIndicator() : base(IndicatorType.CandleBearishSideBySideWhiteLines)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBullishAbandonedBaby)]
public sealed class CoreCandleBullishAbandonedBabyIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBullishAbandonedBabyIndicator() : base(IndicatorType.CandleBullishAbandonedBaby)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBearishAbandonedBaby)]
public sealed class CoreCandleBearishAbandonedBabyIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBearishAbandonedBabyIndicator() : base(IndicatorType.CandleBearishAbandonedBaby)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleAdvanceBlock)]
public sealed class CoreCandleAdvanceBlockIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleAdvanceBlockIndicator() : base(IndicatorType.CandleAdvanceBlock)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleDeliberation)]
public sealed class CoreCandleDeliberationIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleDeliberationIndicator() : base(IndicatorType.CandleDeliberation)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleStickSandwich)]
public sealed class CoreCandleStickSandwichIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleStickSandwichIndicator() : base(IndicatorType.CandleStickSandwich)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBullishTasukiGap)]
public sealed class CoreCandleBullishTasukiGapIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBullishTasukiGapIndicator() : base(IndicatorType.CandleBullishTasukiGap)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBearishTasukiGap)]
public sealed class CoreCandleBearishTasukiGapIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBearishTasukiGapIndicator() : base(IndicatorType.CandleBearishTasukiGap)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBullishGapThreeMethods)]
public sealed class CoreCandleBullishGapThreeMethodsIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBullishGapThreeMethodsIndicator() : base(IndicatorType.CandleBullishGapThreeMethods)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBearishGapThreeMethods)]
public sealed class CoreCandleBearishGapThreeMethodsIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBearishGapThreeMethodsIndicator() : base(IndicatorType.CandleBearishGapThreeMethods)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleIdenticalThreeCrows)]
public sealed class CoreCandleIdenticalThreeCrowsIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleIdenticalThreeCrowsIndicator() : base(IndicatorType.CandleIdenticalThreeCrows)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBullishThreeLineStrike)]
public sealed class CoreCandleBullishThreeLineStrikeIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBullishThreeLineStrikeIndicator() : base(IndicatorType.CandleBullishThreeLineStrike)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBearishThreeLineStrike)]
public sealed class CoreCandleBearishThreeLineStrikeIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBearishThreeLineStrikeIndicator() : base(IndicatorType.CandleBearishThreeLineStrike)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleConcealingBabySwallow)]
public sealed class CoreCandleConcealingBabySwallowIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleConcealingBabySwallowIndicator() : base(IndicatorType.CandleConcealingBabySwallow)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleRisingThreeMethods)]
public sealed class CoreCandleRisingThreeMethodsIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleRisingThreeMethodsIndicator() : base(IndicatorType.CandleRisingThreeMethods)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleFallingThreeMethods)]
public sealed class CoreCandleFallingThreeMethodsIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleFallingThreeMethodsIndicator() : base(IndicatorType.CandleFallingThreeMethods)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleMatHold)]
public sealed class CoreCandleMatHoldIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleMatHoldIndicator() : base(IndicatorType.CandleMatHold)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleLadderBottom)]
public sealed class CoreCandleLadderBottomIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleLadderBottomIndicator() : base(IndicatorType.CandleLadderBottom)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBullishBreakaway)]
public sealed class CoreCandleBullishBreakawayIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBullishBreakawayIndicator() : base(IndicatorType.CandleBullishBreakaway)
    {
    }
}

[StockAnalyzerIndicator(IndicatorType.CandleBearishBreakaway)]
public sealed class CoreCandleBearishBreakawayIndicator : CoreCandlePatternIndicatorBase
{
    public CoreCandleBearishBreakawayIndicator() : base(IndicatorType.CandleBearishBreakaway)
    {
    }
}
