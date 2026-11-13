using System.Linq;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// WebAiExpansion plan 03: the lines of one new indicator must be told apart. The generic role colours (decision U-01=A) can collide
/// (RsiColor and StochDColor are the same purple), so the defaults of every plan-03 type are checked for distinct series colours.
/// </summary>
public class PlanThreeDefaultSeriesColorsTests
{
    [Theory]
    [InlineData(IndicatorType.ChandelierExit)]
    [InlineData(IndicatorType.T3)]
    [InlineData(IndicatorType.McGinleyDynamic)]
    [InlineData(IndicatorType.TSI)]
    [InlineData(IndicatorType.StochRSI)]
    [InlineData(IndicatorType.KVO)]
    [InlineData(IndicatorType.VolumeProfileLevels)]
    public void EverySeriesOfTheDefaultHasItsOwnColour(IndicatorType type)
    {
        var settings = DefaultCoreIndicatorSettings.GetDefault().Single(s => s.TypeEnum == type);

        var colours = settings.SeriesColors.Select(c => c.Color).ToList();

        Assert.NotEmpty(colours);
        Assert.Equal(colours.Count, colours.Distinct().Count());
    }
}
