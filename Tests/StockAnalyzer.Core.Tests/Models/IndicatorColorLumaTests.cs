using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class IndicatorColorLumaTests
{
    private const float Tolerance = 1e-6f;

    [Fact]
    public void Luma_OfBlackAndWhite_IsTheScaleEnds()
    {
        Assert.Equal(0f, IndicatorColor.FromRgb(0, 0, 0).Luma(), Tolerance);
        Assert.Equal(1f, IndicatorColor.FromRgb(255, 255, 255).Luma(), Tolerance);
    }

    /// <summary>The Rec. 709 weights: green dominates, blue is weakest.</summary>
    [Fact]
    public void Luma_WeightsTheChannelsByRec709()
    {
        Assert.Equal(0.2126f, IndicatorColor.FromRgb(255, 0, 0).Luma(), Tolerance);
        Assert.Equal(0.7152f, IndicatorColor.FromRgb(0, 255, 0).Luma(), Tolerance);
        Assert.Equal(0.0722f, IndicatorColor.FromRgb(0, 0, 255).Luma(), Tolerance);
    }

    [Fact]
    public void Luma_IgnoresAlpha_AndTheStaticOverloadAgrees()
    {
        var opaque = IndicatorColor.FromArgb(255, 10, 120, 200);
        var translucent = IndicatorColor.FromArgb(16, 10, 120, 200);

        Assert.Equal(opaque.Luma(), translucent.Luma());
        Assert.Equal(opaque.Luma(), IndicatorColor.Luma(10, 120, 200));
    }
}
