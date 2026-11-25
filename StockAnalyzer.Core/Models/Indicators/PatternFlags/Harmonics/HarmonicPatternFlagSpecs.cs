using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Harmonics;

/// <summary>
/// The catalog rows of the Harmonic flags (plan ChartPatterns56 v2, run V2-6; integration spec 4.2, 5.3): Gartley, Butterfly, Bat.
/// All three are bidirectional whole-series types (<c>MinBars = 40</c>) sharing <see cref="CoreHarmonicPatternParameter"/> with type-specific Fibonacci defaults.
/// </summary>
internal static class HarmonicPatternFlagSpecs
{
    internal static PatternFlagSpec[] CreateSpecs() => new[]
    {
        new PatternFlagSpec(
            IndicatorType.HarmonicGartley,
            CoreIndicatorCategory.Harmonic,
            PatternSpecConstants.HarmonicMinBars,
            PatternFlagEvaluationModel.FullSeries,
            HasDirection: true,
            IndicatorDefaultConstants.HarmonicFlagColor,
            typeof(CoreHarmonicPatternParameter),
            BullishSeries: nameof(CoreHarmonicGartleyIndicator.BullishGartley),
            BearishSeries: nameof(CoreHarmonicGartleyIndicator.BearishGartley),
            ParameterFactory: () => CoreHarmonicPatternParameter.CreateFor(IndicatorType.HarmonicGartley)),
        new PatternFlagSpec(
            IndicatorType.HarmonicButterfly,
            CoreIndicatorCategory.Harmonic,
            PatternSpecConstants.HarmonicMinBars,
            PatternFlagEvaluationModel.FullSeries,
            HasDirection: true,
            IndicatorDefaultConstants.HarmonicFlagColor,
            typeof(CoreHarmonicPatternParameter),
            BullishSeries: nameof(CoreHarmonicButterflyIndicator.BullishButterfly),
            BearishSeries: nameof(CoreHarmonicButterflyIndicator.BearishButterfly),
            ParameterFactory: () => CoreHarmonicPatternParameter.CreateFor(IndicatorType.HarmonicButterfly)),
        new PatternFlagSpec(
            IndicatorType.HarmonicBat,
            CoreIndicatorCategory.Harmonic,
            PatternSpecConstants.HarmonicMinBars,
            PatternFlagEvaluationModel.FullSeries,
            HasDirection: true,
            IndicatorDefaultConstants.HarmonicFlagColor,
            typeof(CoreHarmonicPatternParameter),
            BullishSeries: nameof(CoreHarmonicBatIndicator.BullishBat),
            BearishSeries: nameof(CoreHarmonicBatIndicator.BearishBat),
            ParameterFactory: () => CoreHarmonicPatternParameter.CreateFor(IndicatorType.HarmonicBat)),
    };
}
