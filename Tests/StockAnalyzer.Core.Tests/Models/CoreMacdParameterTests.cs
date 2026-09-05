using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreMacdParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsLongPlusSignalMinusOne()
    {
        var param = new CoreMacdParameter();

        Assert.Equal(34, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_CustomPeriods_MatchesFormula()
    {
        var param = new CoreMacdParameter { ShortPeriod = 5, LongPeriod = 40, SignalPeriod = 5 };

        Assert.Equal(44, param.GetRequiredWarmupBars());
    }
}
