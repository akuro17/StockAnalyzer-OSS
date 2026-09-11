using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreCoppockCurveParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsMaxOfRocPeriodsPlusWmaPeriod()
    {
        var param = new CoreCoppockCurveParameter();

        Assert.Equal(24, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_ShortRocPeriodLargerThanLong_ReturnsShortRocPeriodPlusWma()
    {
        var param = new CoreCoppockCurveParameter { LongRocPeriod = 14, ShortRocPeriod = 30, WmaPeriod = 10 };

        Assert.Equal(40, param.GetRequiredWarmupBars());
    }
}
