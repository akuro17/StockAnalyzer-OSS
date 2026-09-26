using System.Text.Json;
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

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(40)]
    public void GetFutureProjectionBars_FollowsDisplacement(int displacement)
    {
        var param = new CoreIchimokuParameter { Offset = displacement };

        Assert.Equal(displacement, param.GetFutureProjectionBars());
    }

    [Fact]
    public void GetFutureProjectionBars_DefaultValues_IsTheStandardDisplacement()
    {
        Assert.Equal(CoreIchimokuParameter.DefaultDisplacement, new CoreIchimokuParameter().GetFutureProjectionBars());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-26)]
    [InlineData(int.MinValue)]
    public void Offset_NegativeValue_IsClampedToTheMinimum_FromEveryEntryPoint(int negative)
    {
        var assigned = new CoreIchimokuParameter { Offset = negative };
        var loaded = JsonSerializer.Deserialize<CoreIchimokuParameter>($"{{\"Offset\":{negative}}}");

        Assert.Equal(CoreIchimokuParameter.MinDisplacement, assigned.Offset);
        Assert.Equal(CoreIchimokuParameter.MinDisplacement, loaded!.Offset);
        Assert.Equal(0, assigned.GetFutureProjectionBars());
    }

    [Fact]
    public void GetFutureProjectionBars_IndicatorWithoutProjection_DefaultsToZero()
    {
        Assert.Equal(0, new CoreMacdParameter().GetFutureProjectionBars());
    }
}
