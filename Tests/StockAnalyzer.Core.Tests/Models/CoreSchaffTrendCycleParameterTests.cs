using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreSchaffTrendCycleParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsLongPlusCycleTimesThree()
    {
        var param = new CoreSchaffTrendCycleParameter();

        Assert.Equal(180, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_CyclePeriodLargerThanLongPeriod_ReturnsFormula()
    {
        var param = new CoreSchaffTrendCycleParameter { CyclePeriod = 100, ShortPeriod = 23, LongPeriod = 50 };

        Assert.Equal(450, param.GetRequiredWarmupBars());
    }
}
