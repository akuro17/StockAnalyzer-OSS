using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreStochasticParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsKPeriodPlusSmoothPlusDPeriodMinusTwo()
    {
        var param = new CoreStochasticParameter();

        Assert.Equal(18, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_CustomPeriods_MatchesFormula()
    {
        var param = new CoreStochasticParameter { KPeriod = 20, DPeriod = 5, Smooth = 4 };

        Assert.Equal(27, param.GetRequiredWarmupBars());
    }
}
