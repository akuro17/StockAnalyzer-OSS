using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>Unit tests of the named limits that replaced the literals of <see cref="NurbsMath"/> and the drawing objects.</summary>
public class NurbsMathLimitsTests
{
    [Theory]
    [InlineData(0, 40)]    // below the minimum
    [InlineData(1, 40)]
    [InlineData(2, 40)]    // 2 * 20 = 40 = the minimum
    [InlineData(3, 60)]    // inside the range: 20 per control point
    [InlineData(6, 120)]
    [InlineData(7, 128)]   // 7 * 20 = 140 -> the maximum
    [InlineData(500, 128)]
    public void ResolveSampleCount_IsTwentyPerControlPointClampedToFortyAndOneHundredTwentyEight(int controlPoints, int expected)
    {
        Assert.Equal(expected, NurbsMath.ResolveSampleCount(controlPoints));
    }

    [Fact]
    public void StackAllocElementThreshold_KeepsTheValueTheDrawingObjectsUsedBeforeItWasNamed()
    {
        Assert.Equal(128, DrawingBufferLimits.StackAllocElementThreshold);
    }

    [Fact]
    public void MaxKnotCount_KeepsTheValueTheNurbsCodeUsedBeforeItWasNamed()
    {
        Assert.Equal(256, NurbsMath.MaxKnotCount);
    }
}
