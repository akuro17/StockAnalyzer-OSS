using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services.Analysis.PatternEngines;

namespace StockAnalyzer.Core.Models.Indicators.PatternFlags.Rules;

/// <summary>
/// The catalog rows of the Trading Rules flags (plan ChartPatterns56 v2, run V2-7; integration spec 4.2, 5.4):
/// Granville's Law Buy Signal (<c>Buy1..Buy4</c>) and Sell Signal (<c>Sell1..Sell4</c>), Group B multi-rule indicators with hidden <c>Main</c> and <c>RuleCode</c>.
/// </summary>
internal static class RulePatternFlagSpecs
{
    internal static PatternFlagSpec[] CreateSpecs() => new[]
    {
        new PatternFlagSpec(
            IndicatorType.GranvilleBuySignal,
            CoreIndicatorCategory.TradingRules,
            PatternSpecConstants.GranvilleDefaultMinBars,
            PatternFlagEvaluationModel.FullSeries,
            HasDirection: false,
            IndicatorDefaultConstants.PatternFlagBullishColor,
            typeof(CoreGranvilleSignalParameter),
            RuleSeries: new[]
            {
                nameof(CoreGranvilleBuySignalIndicator.Buy1),
                nameof(CoreGranvilleBuySignalIndicator.Buy2),
                nameof(CoreGranvilleBuySignalIndicator.Buy3),
                nameof(CoreGranvilleBuySignalIndicator.Buy4),
            },
            RuleColors: new[]
            {
                IndicatorDefaultConstants.PatternFlagBullishColor,
                IndicatorDefaultConstants.PatternRecognitionFlagColor,
                IndicatorDefaultConstants.PatternFlagGranvilleBuy3Color,
                IndicatorDefaultConstants.HarmonicFlagColor,
            }),
        new PatternFlagSpec(
            IndicatorType.GranvilleSellSignal,
            CoreIndicatorCategory.TradingRules,
            PatternSpecConstants.GranvilleDefaultMinBars,
            PatternFlagEvaluationModel.FullSeries,
            HasDirection: false,
            IndicatorDefaultConstants.PatternFlagBearishColor,
            typeof(CoreGranvilleSignalParameter),
            RuleSeries: new[]
            {
                nameof(CoreGranvilleSellSignalIndicator.Sell1),
                nameof(CoreGranvilleSellSignalIndicator.Sell2),
                nameof(CoreGranvilleSellSignalIndicator.Sell3),
                nameof(CoreGranvilleSellSignalIndicator.Sell4),
            },
            RuleColors: new[]
            {
                IndicatorDefaultConstants.PatternFlagBearishColor,
                IndicatorDefaultConstants.MarketStructureFlagColor,
                IndicatorDefaultConstants.PatternFlagGranvilleSell3Color,
                IndicatorDefaultConstants.PatternFlagGranvilleSell4Color,
            }),
    };
}
