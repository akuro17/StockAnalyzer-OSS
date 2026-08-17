using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Regression coverage for LongShortPositionObject's clamp helpers, consolidated from
/// previously-duplicated logic in the settings dialog and chart drag-handle interaction.
/// Proves the consolidation preserved the original numeric behavior of both call sites.
/// </summary>
public class LongShortPositionClampTests
{
    [Theory]
    [InlineData(true, 90, 100)] // Long, Stop already below Entry -> unchanged
    [InlineData(false, 110, 100)] // Short, Stop already above Entry -> unchanged
    public void ClampStopPrice_ValidSide_ReturnsUnchanged(bool isLong, decimal validStop, decimal entry)
    {
        var result = LongShortPositionObject.ClampStopPrice(validStop, entry, isLong);
        Assert.Equal(validStop, result);
    }

    [Fact]
    public void ClampStopPrice_Long_StopAtOrAboveEntry_ClampsBelowEntry()
    {
        Assert.Equal(100m - ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampStopPrice(100m, 100m, isLong: true));
        Assert.Equal(100m - ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampStopPrice(105m, 100m, isLong: true));
    }

    [Fact]
    public void ClampStopPrice_Short_StopAtOrBelowEntry_ClampsAboveEntry()
    {
        Assert.Equal(100m + ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampStopPrice(100m, 100m, isLong: false));
        Assert.Equal(100m + ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampStopPrice(95m, 100m, isLong: false));
    }

    [Fact]
    public void ClampTargetPrice_Long_TargetAtOrBelowEntry_ClampsAboveEntry()
    {
        Assert.Equal(100m + ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampTargetPrice(100m, 100m, isLong: true));
        Assert.Equal(100m + ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampTargetPrice(95m, 100m, isLong: true));
    }

    [Fact]
    public void ClampTargetPrice_Short_TargetAtOrAboveEntry_ClampsBelowEntry()
    {
        Assert.Equal(100m - ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampTargetPrice(100m, 100m, isLong: false));
        Assert.Equal(100m - ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampTargetPrice(105m, 100m, isLong: false));
    }

    [Fact]
    public void ClampEntryPrice_Long_WithinStopTargetRange_ReturnsUnchanged()
    {
        Assert.Equal(100m, LongShortPositionObject.ClampEntryPrice(100m, stopPrice: 90m, targetPrice: 110m, isLong: true));
    }

    [Fact]
    public void ClampEntryPrice_Long_AtOrPastStop_ClampsAboveStop()
    {
        Assert.Equal(90m + ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampEntryPrice(85m, stopPrice: 90m, targetPrice: 110m, isLong: true));
    }

    [Fact]
    public void ClampEntryPrice_Long_AtOrPastTarget_ClampsBelowTarget()
    {
        Assert.Equal(110m - ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampEntryPrice(115m, stopPrice: 90m, targetPrice: 110m, isLong: true));
    }

    [Fact]
    public void ClampEntryPrice_Short_WithinTargetStopRange_ReturnsUnchanged()
    {
        Assert.Equal(100m, LongShortPositionObject.ClampEntryPrice(100m, stopPrice: 110m, targetPrice: 90m, isLong: false));
    }

    [Fact]
    public void ClampEntryPrice_Short_AtOrPastStop_ClampsBelowStop()
    {
        Assert.Equal(110m - ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampEntryPrice(115m, stopPrice: 110m, targetPrice: 90m, isLong: false));
    }

    [Fact]
    public void ClampEntryPrice_Short_AtOrPastTarget_ClampsAboveTarget()
    {
        Assert.Equal(90m + ChartConstants.LongShortPriceClampEpsilon, LongShortPositionObject.ClampEntryPrice(85m, stopPrice: 110m, targetPrice: 90m, isLong: false));
    }
}
