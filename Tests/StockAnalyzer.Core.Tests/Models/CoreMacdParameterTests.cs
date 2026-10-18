using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreMacdParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsSlowPlusSignalTimesThree()
    {
        var param = new CoreMacdParameter();

        Assert.Equal(105, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_CustomPeriods_MatchesFormula()
    {
        var param = new CoreMacdParameter { ShortPeriod = 5, LongPeriod = 40, SignalPeriod = 5 };

        Assert.Equal(135, param.GetRequiredWarmupBars());
    }
}
