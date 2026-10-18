using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreTrixParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsNineTimesPeriodPlusOne()
    {
        var param = new CoreTrixParameter();

        Assert.Equal(136, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_CustomPeriod_MatchesFormula()
    {
        var param = new CoreTrixParameter { Period = 10 };

        Assert.Equal(91, param.GetRequiredWarmupBars());
    }
}
