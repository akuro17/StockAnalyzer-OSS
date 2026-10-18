using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreSmiParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsPeriodPlusSmoothingFormula()
    {
        var param = new CoreSmiParameter();

        Assert.Equal(53, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_CustomValues_MatchesFormula()
    {
        var param = new CoreSmiParameter { Period = 20, Smooth1 = 8, Smooth2 = 6, SignalPeriod = 5 };

        Assert.Equal(77, param.GetRequiredWarmupBars());
    }
}
