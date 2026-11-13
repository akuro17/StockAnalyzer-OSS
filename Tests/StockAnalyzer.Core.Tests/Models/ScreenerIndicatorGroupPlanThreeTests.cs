using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// WebAiExpansion plan 03 (owner decision 2026-11-14, Q2=A): every new indicator is registered explicitly in the Screener group of its classification,
/// not left to the "Trend" fallback of the catalog builder, and in exactly one group.
/// </summary>
public class ScreenerIndicatorGroupPlanThreeTests
{
    [Theory]
    [InlineData(IndicatorType.ChandelierExit, "Trend")]
    [InlineData(IndicatorType.T3, "Trend")]
    [InlineData(IndicatorType.McGinleyDynamic, "Trend")]
    [InlineData(IndicatorType.TSI, "Oscillator")]
    [InlineData(IndicatorType.StochRSI, "Oscillator")]
    [InlineData(IndicatorType.KVO, "Volume")]
    [InlineData(IndicatorType.VolumeProfileLevels, "Chart")]
    public void ANewIndicatorIsListedInExactlyItsOwnGroup(IndicatorType type, string expectedGroup)
    {
        var groups = ScreenerIndicatorGroup.GetDefaultGroups()
            .Where(g => g.CategoryType == "Indicator" && g.IndicatorTypes.Contains(type))
            .Select(g => g.Name)
            .ToList();

        Assert.Equal(new[] { expectedGroup }, groups);
    }
}
