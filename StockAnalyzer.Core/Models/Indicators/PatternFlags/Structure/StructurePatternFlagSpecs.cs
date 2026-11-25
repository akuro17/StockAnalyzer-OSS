using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Structure;

/// <summary>The catalog rows of the Market Structure flags (plan ChartPatterns56 v2, runs V2-5 and V2-10; all full-series Model P engines with first value at bar 20).</summary>
internal static class StructurePatternFlagSpecs
{
    internal static PatternFlagSpec[] CreateSpecs() => new[]
    {
        new PatternFlagSpec(IndicatorType.MarketStructureBos, CoreIndicatorCategory.MarketStructure, PatternSpecConstants.StructureShiftMinBars, PatternFlagEvaluationModel.FullSeries, HasDirection: true,
            IndicatorDefaultConstants.MarketStructureFlagColor, typeof(CoreStructureShiftParameter),
            BullishSeries: nameof(CoreMarketStructureBosIndicator.BullishBos), BearishSeries: nameof(CoreMarketStructureBosIndicator.BearishBos),
            ParameterFactory: () => CoreStructureShiftParameter.CreateFor(IndicatorType.MarketStructureBos)),
        new PatternFlagSpec(IndicatorType.MarketStructureChoch, CoreIndicatorCategory.MarketStructure, PatternSpecConstants.StructureShiftMinBars, PatternFlagEvaluationModel.FullSeries, HasDirection: true,
            IndicatorDefaultConstants.MarketStructureFlagColor, typeof(CoreStructureShiftParameter),
            BullishSeries: nameof(CoreMarketStructureChochIndicator.BullishChoch), BearishSeries: nameof(CoreMarketStructureChochIndicator.BearishChoch),
            ParameterFactory: () => CoreStructureShiftParameter.CreateFor(IndicatorType.MarketStructureChoch)),
        new PatternFlagSpec(IndicatorType.MarketStructureHhHl, CoreIndicatorCategory.MarketStructure, PatternSpecConstants.StructureTrendMinBars, PatternFlagEvaluationModel.FullSeries, HasDirection: false,
            IndicatorDefaultConstants.MarketStructureFlagColor, typeof(CoreStructureTrendParameter)),
        new PatternFlagSpec(IndicatorType.MarketStructureLlLh, CoreIndicatorCategory.MarketStructure, PatternSpecConstants.StructureTrendMinBars, PatternFlagEvaluationModel.FullSeries, HasDirection: false,
            IndicatorDefaultConstants.MarketStructureFlagColor, typeof(CoreStructureTrendParameter)),
    };
}
