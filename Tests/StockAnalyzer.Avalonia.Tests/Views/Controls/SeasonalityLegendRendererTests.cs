using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

/// <summary>Pure-logic tests of the shared seasonality legend helpers (the drawing itself is covered by the render-threading tests).</summary>
public class SeasonalityLegendRendererTests
{
    private const float TextSize = 12f;
    private const double BoundsHeight = 120d;
    private const int RowCount = 30;

    // Pointer inside the legend hit box: x within [LegendMargin, LegendMargin + LegendHitWidth], y within [LegendMargin, LegendMargin + viewport].
    private const double InsideX = SeasonalityLegendRenderer.LegendMargin + 10d;
    private const double InsideY = SeasonalityLegendRenderer.LegendMargin + 10d;

    [Fact]
    public void TryComputeScrollOffset_NoRows_ReturnsFalseAndKeepsOffset()
    {
        bool moved = SeasonalityLegendRenderer.TryComputeScrollOffset(0, TextSize, BoundsHeight, InsideX, InsideY, -1d, 7f, out float updated);

        Assert.False(moved);
        Assert.Equal(7f, updated);
    }

    [Fact]
    public void TryComputeScrollOffset_LegendFitsTheViewport_ReturnsFalse()
    {
        bool moved = SeasonalityLegendRenderer.TryComputeScrollOffset(2, TextSize, BoundsHeight, InsideX, InsideY, -1d, 0f, out float updated);

        Assert.False(moved);
        Assert.Equal(0f, updated);
    }

    [Theory]
    [InlineData(SeasonalityLegendRenderer.LegendMargin - 1d, InsideY)]
    [InlineData(SeasonalityLegendRenderer.LegendMargin + SeasonalityLegendRenderer.LegendHitWidth + 1d, InsideY)]
    [InlineData(InsideX, SeasonalityLegendRenderer.LegendMargin - 1d)]
    [InlineData(InsideX, BoundsHeight)]
    public void TryComputeScrollOffset_PointerOutsideTheLegendColumn_ReturnsFalse(double x, double y)
    {
        bool moved = SeasonalityLegendRenderer.TryComputeScrollOffset(RowCount, TextSize, BoundsHeight, x, y, -1d, 0f, out float updated);

        Assert.False(moved);
        Assert.Equal(0f, updated);
    }

    [Fact]
    public void TryComputeScrollOffset_WheelDown_AdvancesByTwoRowsPerNotch()
    {
        float rowHeight = System.Math.Max(SeasonalityLegendRenderer.LegendRowHeight, TextSize + SeasonalityLegendRenderer.LegendRowExtraGap);

        bool moved = SeasonalityLegendRenderer.TryComputeScrollOffset(RowCount, TextSize, BoundsHeight, InsideX, InsideY, -1d, 0f, out float updated);

        Assert.True(moved);
        Assert.Equal(rowHeight * SeasonalityLegendRenderer.LegendWheelRowsPerNotch, updated);
    }

    [Fact]
    public void TryComputeScrollOffset_AtBothEnds_ClampsAndReportsNoMovement()
    {
        float rowHeight = System.Math.Max(SeasonalityLegendRenderer.LegendRowHeight, TextSize + SeasonalityLegendRenderer.LegendRowExtraGap);
        float viewportHeight = (float)BoundsHeight - (2f * SeasonalityLegendRenderer.LegendMargin);
        float maxScroll = (RowCount * rowHeight) - viewportHeight;

        bool atTop = SeasonalityLegendRenderer.TryComputeScrollOffset(RowCount, TextSize, BoundsHeight, InsideX, InsideY, 1d, 0f, out float topOffset);
        bool pastEnd = SeasonalityLegendRenderer.TryComputeScrollOffset(RowCount, TextSize, BoundsHeight, InsideX, InsideY, -1000d, 0f, out float endOffset);
        bool atEnd = SeasonalityLegendRenderer.TryComputeScrollOffset(RowCount, TextSize, BoundsHeight, InsideX, InsideY, -1d, maxScroll, out float stillEnd);

        Assert.False(atTop);
        Assert.Equal(0f, topOffset);
        Assert.True(pastEnd);
        Assert.Equal(maxScroll, endOffset);
        Assert.False(atEnd);
        Assert.Equal(maxScroll, stillEnd);
    }

    [Fact]
    public void RenderCache_Palette_ReusesTheConversionForTheSameListAndRebuildsForANewOne()
    {
        var cache = new SeasonalityPlotRenderCache();
        IReadOnlyList<Color> first = new[] { Colors.Red, Colors.Lime };
        IReadOnlyList<Color> second = new[] { Colors.Red, Colors.Lime };

        var a = cache.ResolvePalette(first);
        var b = cache.ResolvePalette(first);
        var c = cache.ResolvePalette(second);

        Assert.NotNull(a);
        Assert.Same(a, b);
        Assert.NotSame(a, c);
        Assert.Equal(2, c!.Length);
        Assert.Null(cache.ResolvePalette(null));
        Assert.Null(cache.ResolvePalette(System.Array.Empty<Color>()));
    }

    [Fact]
    public void RenderCache_YearStates_ReusesTheLookupForTheSameListAndRebuildsForANewOne()
    {
        var cache = new SeasonalityPlotRenderCache();
        IReadOnlyList<SeasonalityYearDrawState> first = new[] { new SeasonalityYearDrawState(2023, true, 1f), new SeasonalityYearDrawState(2024, false, 1.5f) };
        IReadOnlyList<SeasonalityYearDrawState> second = new[] { new SeasonalityYearDrawState(2024, true, 1f) };

        var a = cache.ResolveYearStates(first);
        var b = cache.ResolveYearStates(first);
        var c = cache.ResolveYearStates(second);

        Assert.NotNull(a);
        Assert.Same(a, b);
        Assert.False(a![2024].IsVisible);
        Assert.NotSame(a, c);
        Assert.Single(c!);
        Assert.Null(cache.ResolveYearStates(null));
    }
}
