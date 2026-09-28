using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreSmiParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsPeriodPlusSmooth1PlusSmooth2MinusTwo()
    {
        var param = new CoreSmiParameter();

        Assert.Equal(20, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_CustomValues_MatchesFormula()
    {
        var param = new CoreSmiParameter { Period = 20, Smooth1 = 8, Smooth2 = 6 };

        Assert.Equal(32, param.GetRequiredWarmupBars());
    }
}
