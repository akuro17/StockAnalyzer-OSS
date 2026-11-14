using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// The members of <see cref="IndicatorType"/> are append-only: serialized templates and settings store them, so an ordinal that moves silently changes what a saved file means.
/// This pins the ordinals of the types WebAiExpansion plans 03 and 04 touched (the three former placeholders keep their old position; the plan-03 and plan-04 types are the last members).
/// Adding a type at the end needs no change here; inserting or reordering a member fails this test on purpose.
/// </summary>
public class IndicatorTypeOrdinalPinTests
{
    [Theory]
    [InlineData(IndicatorType.Garch11, 68)]
    [InlineData(IndicatorType.HurstExponent, 81)]
    [InlineData(IndicatorType.Entropy, 113)]
    [InlineData(IndicatorType.ChandelierExit, 193)]
    [InlineData(IndicatorType.T3, 194)]
    [InlineData(IndicatorType.McGinleyDynamic, 195)]
    [InlineData(IndicatorType.TSI, 196)]
    [InlineData(IndicatorType.StochRSI, 197)]
    [InlineData(IndicatorType.KVO, 198)]
    [InlineData(IndicatorType.VolumeProfileLevels, 199)]
    [InlineData(IndicatorType.ParkinsonVolatility, 200)]
    [InlineData(IndicatorType.RogersSatchellVolatility, 201)]
    public void TheOrdinalOfATouchedTypeIsPinned(IndicatorType type, int ordinal) => Assert.Equal(ordinal, (int)type);
}
