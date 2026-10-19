using System.Text.Json;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class CoreIchimokuParameterTests
{
    [Fact]
    public void GetRequiredWarmupBars_DefaultValues_ReturnsMaxSamplePlusDisplacement()
    {
        var param = new CoreIchimokuParameter();

        Assert.Equal(78, param.GetRequiredWarmupBars());
    }

    [Fact]
    public void GetRequiredWarmupBars_TenkanIsLargest_UsesTenkanSample()
    {
        var param = new CoreIchimokuParameter { TenkanSample = 60, KijunSample = 26, SenkouBSample = 52 };

        Assert.Equal(86, param.GetRequiredWarmupBars());
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

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_InvalidPeriods_ThrowsArgumentOutOfRangeException(int invalidValue)
    {
        var paramTenkan = new CoreIchimokuParameter { TenkanSample = invalidValue };
        Assert.Throws<System.ArgumentOutOfRangeException>(() => paramTenkan.Validate());

        var paramKijun = new CoreIchimokuParameter { KijunSample = invalidValue };
        Assert.Throws<System.ArgumentOutOfRangeException>(() => paramKijun.Validate());

        var paramSenkouB = new CoreIchimokuParameter { SenkouBSample = invalidValue };
        Assert.Throws<System.ArgumentOutOfRangeException>(() => paramSenkouB.Validate());
    }

    [Fact]
    public void Validate_ValidPeriods_DoesNotThrow()
    {
        var param = new CoreIchimokuParameter { TenkanSample = 9, KijunSample = 26, SenkouBSample = 52 };
        param.Validate();
    }
}
