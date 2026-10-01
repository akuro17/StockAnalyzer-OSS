using System;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>
/// <see cref="EquityPlotArea"/> replaced the control's inline pixel math. These tests pin it to that earlier arithmetic
/// bit-for-bit (exact equality, no tolerance) so the refactor is provably free of side effects.
/// </summary>
public class EquityPlotAreaTests
{
    // Plot rectangles as BuildSnapshot derives them: left/top are margins, right/bottom come from awkward control sizes.
    private static readonly (double Left, double Top, double Right, double Bottom)[] Rects =
    {
        (64d, 16d, 400d - 16d, 240d - 32d),
        (64d, 16d, 437.3d - 16d, 261.7d - 32d),
        (64d, 16d, 1234.5678d - 16d, 0.1d + 48.0001d),
        (64d, 16d, 64d + 0.3d, 16d + 0.7d),
    };

    private static readonly double[] Fractions = { 0d, 1d, 0.5d, 1d / 3d, 2d / 3d, 0.1d, 0.7d, 0.123456789d, 1e-9d, 1d - 1e-9d };

    [Fact]
    public void ToScreen_MatchesThePreviousInlineFormula_ExactlyForEveryRectAndFraction()
    {
        foreach ((double left, double top, double right, double bottom) in Rects)
        {
            var plot = new EquityPlotArea(left, top, right, bottom);
            double width = right - left;
            double height = bottom - top;
            foreach (double xf in Fractions)
            foreach (double yf in Fractions)
            {
                (double x, double y) = plot.ToScreen(new EquityCurveNormalizedPoint(xf, yf));

                Assert.Equal(left + xf * width, x);
                Assert.Equal(bottom - yf * height, y);
            }
        }
    }

    [Fact]
    public void WidthAndHeight_MatchThePreviousInlineFormulas_Exactly()
    {
        foreach ((double left, double top, double right, double bottom) in Rects)
        {
            var plot = new EquityPlotArea(left, top, right, bottom);

            Assert.Equal(right - left, plot.Width);
            Assert.Equal(bottom - top, plot.Height);
        }
    }

    [Fact]
    public void Corners_MapToTheRectangleCorners()
    {
        var plot = new EquityPlotArea(64d, 16d, 384d, 208d);

        Assert.Equal((64d, 208d), plot.ToScreen(new EquityCurveNormalizedPoint(0d, 0d)));
        Assert.Equal((384d, 16d), plot.ToScreen(new EquityCurveNormalizedPoint(1d, 1d)));
    }

    [Theory]
    [InlineData(64d, 16d, 384d, 208d, 64d, 16d, true)]    // top-left corner (edges are inside)
    [InlineData(64d, 16d, 384d, 208d, 384d, 208d, true)]  // bottom-right corner
    [InlineData(64d, 16d, 384d, 208d, 224d, 100d, true)]
    [InlineData(64d, 16d, 384d, 208d, 63.9d, 100d, false)]
    [InlineData(64d, 16d, 384d, 208d, 384.1d, 100d, false)]
    [InlineData(64d, 16d, 384d, 208d, 224d, 15.9d, false)]
    [InlineData(64d, 16d, 384d, 208d, 224d, 208.1d, false)]
    public void Contains_IsTrueInsideAndOnTheEdges(double left, double top, double right, double bottom, double x, double y, bool expected)
    {
        Assert.Equal(expected, new EquityPlotArea(left, top, right, bottom).Contains(x, y));
    }

    [Theory]
    [InlineData(64d, 16d, 384d, 208d, true)]
    [InlineData(64d, 16d, 64d, 208d, false)]   // zero width
    [InlineData(64d, 16d, 384d, 16d, false)]   // zero height
    [InlineData(64d, 16d, 40d, 208d, false)]   // negative width (control narrower than the margins)
    [InlineData(64d, 16d, 384d, 8d, false)]    // negative height
    [InlineData(64d, 16d, double.NaN, 208d, false)]
    [InlineData(64d, 16d, double.PositiveInfinity, 208d, false)]
    public void IsDrawable_RequiresFinitePositiveExtents(double left, double top, double right, double bottom, bool expected)
    {
        Assert.Equal(expected, new EquityPlotArea(left, top, right, bottom).IsDrawable);
    }
}
