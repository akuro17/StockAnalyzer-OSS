using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Formations;

/// <summary>
/// The catalog rows of the Pattern Recognition flags (plan ChartPatterns56, section 5.2): Head and Shoulders, Double Top / Bottom, Flag. All are bidirectional. Head and Shoulders and Double Top / Bottom are
/// whole-series types of the pattern engines (run V2-3); Flag is a whole-series type of the flag engine (run V2-4).
/// </summary>
internal static class FormationPatternFlagSpecs
{
    internal static PatternFlagSpec[] CreateSpecs() => new[]
    {
        new PatternFlagSpec(IndicatorType.PatternHeadAndShoulders, CoreIndicatorCategory.PatternRecognition, PatternSpecConstants.FormationMinBars, PatternFlagEvaluationModel.FullSeries, HasDirection: true,
            IndicatorDefaultConstants.PatternRecognitionFlagColor, typeof(CoreHeadAndShouldersParameter),
            BullishSeries: nameof(CorePatternHeadAndShouldersIndicator.InverseBottom), BearishSeries: nameof(CorePatternHeadAndShouldersIndicator.Top)),
        new PatternFlagSpec(IndicatorType.PatternDoubleTopBottom, CoreIndicatorCategory.PatternRecognition, PatternSpecConstants.FormationMinBars, PatternFlagEvaluationModel.FullSeries, HasDirection: true,
            IndicatorDefaultConstants.PatternRecognitionFlagColor, typeof(CoreDoubleTopBottomParameter),
            BullishSeries: nameof(CorePatternDoubleTopBottomIndicator.DoubleBottom), BearishSeries: nameof(CorePatternDoubleTopBottomIndicator.DoubleTop)),
        new PatternFlagSpec(IndicatorType.PatternFlag, CoreIndicatorCategory.PatternRecognition, PatternSpecConstants.FlagMinBars, PatternFlagEvaluationModel.FullSeries, HasDirection: true,
            IndicatorDefaultConstants.PatternRecognitionFlagColor, typeof(CoreFlagPatternParameter),
            BullishSeries: nameof(CorePatternFlagIndicator.BullishFlag), BearishSeries: nameof(CorePatternFlagIndicator.BearishFlag)),
    };
}
