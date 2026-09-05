using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreSchaffTrendCycleParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsLongPeriod()
    {
        var param = new CoreSchaffTrendCycleParameter();

        Assert.Equal(50, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_CyclePeriodLargerThanLongPeriod_ReturnsCyclePeriod()
    {
        var param = new CoreSchaffTrendCycleParameter { CyclePeriod = 100, ShortPeriod = 23, LongPeriod = 50 };

        Assert.Equal(100, param.GetRequiredWarmupBars());
    }
}
