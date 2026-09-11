using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Trend;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

/// <summary>
/// Moving Average Cross previously had no convention-named parameter class and a Configure() that bound to
/// CoreDmaParameter with an inverted field mapping, so it rendered as "No parameters" in every settings
/// dialog. These tests lock in that its short/long periods are now discoverable and configurable.
/// </summary>
public class CoreMovingAverageCrossParameterTests
{
    [Fact]
    public void GetDefaultSettings_ProducesMovingAverageCrossParameterWithClassDefaults()
    {
        var indicator = IndicatorFactory.Default.Create(IndicatorType.MovingAverageCross);
        Assert.NotNull(indicator);

        var settings = indicator!.GetDefaultSettings();

        var param = Assert.IsType<CoreMovingAverageCrossParameter>(settings.ParameterObject);
        Assert.Equal(10, param.ShortPeriod);
        Assert.Equal(20, param.LongPeriod);
    }

    [Fact]
    public void Configure_AppliesShortAndLongPeriodOneToOne()
    {
        var indicator = new CoreMovingAverageCrossIndicator();

        indicator.Configure(new CoreMovingAverageCrossParameter { ShortPeriod = 5, LongPeriod = 40 });

        Assert.Equal(5, indicator.ShortPeriod);
        Assert.Equal(40, indicator.LongPeriod);
    }

    [Fact]
    public void GetDisplayName_MatchesShortLongOrder()
    {
        var param = new CoreMovingAverageCrossParameter { ShortPeriod = 8, LongPeriod = 21 };

        Assert.Equal("MA Cross (8, 21)", param.GetDisplayName("MA Cross"));
    }

    [Fact]
    public void GetRequiredWarmupBars_ReturnsLongPeriod_WhenLongIsLarger()
    {
        var param = new CoreMovingAverageCrossParameter { ShortPeriod = 10, LongPeriod = 20 };

        Assert.Equal(20, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_ReturnsShortPeriod_WhenShortIsLargerDueToMisconfiguration()
    {
        var param = new CoreMovingAverageCrossParameter { ShortPeriod = 50, LongPeriod = 20 };

        Assert.Equal(50, param.GetRequiredWarmupBars());
    }
}
