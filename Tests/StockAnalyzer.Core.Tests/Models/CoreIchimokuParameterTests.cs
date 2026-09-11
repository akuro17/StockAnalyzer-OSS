using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreIchimokuParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsMaxSampleMinusOne()
    {
        var param = new CoreIchimokuParameter();

        Assert.Equal(51, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_TenkanIsLargest_UsesTenkanSample()
    {
        var param = new CoreIchimokuParameter { TenkanSample = 60, KijunSample = 26, SenkouBSample = 52 };

        Assert.Equal(59, param.GetRequiredWarmupBars());
    }
}
