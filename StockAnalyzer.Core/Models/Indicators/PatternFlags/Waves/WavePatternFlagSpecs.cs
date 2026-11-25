using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Waves;

/// <summary>
/// The catalog rows of the Wave Theory flags (plan ChartPatterns56 v2, run V2-7; integration spec 4.2, 5.5):
/// Elliott Wave Impulse 1-5 and Corrective ABC. Both are bidirectional whole-series types (<c>MinBars = 40</c>) in independent pools.
/// </summary>
internal static class WavePatternFlagSpecs
{
    internal static PatternFlagSpec[] CreateSpecs() => new[]
    {
        new PatternFlagSpec(
            IndicatorType.ElliottWaveImpulse,
            CoreIndicatorCategory.WaveTheory,
            PatternSpecConstants.ElliottMinBars,
            PatternFlagEvaluationModel.FullSeries,
            HasDirection: true,
            IndicatorDefaultConstants.WaveTheoryFlagColor,
            typeof(CoreElliottImpulseParameter),
            BullishSeries: nameof(CoreElliottWaveImpulseIndicator.BullishImpulse),
            BearishSeries: nameof(CoreElliottWaveImpulseIndicator.BearishImpulse)),
        new PatternFlagSpec(
            IndicatorType.ElliottWaveCorrective,
            CoreIndicatorCategory.WaveTheory,
            PatternSpecConstants.ElliottMinBars,
            PatternFlagEvaluationModel.FullSeries,
            HasDirection: true,
            IndicatorDefaultConstants.WaveTheoryFlagColor,
            typeof(CoreElliottCorrectiveParameter),
            BullishSeries: nameof(CoreElliottWaveCorrectiveIndicator.BullishCorrective),
            BearishSeries: nameof(CoreElliottWaveCorrectiveIndicator.BearishCorrective)),
    };
}
