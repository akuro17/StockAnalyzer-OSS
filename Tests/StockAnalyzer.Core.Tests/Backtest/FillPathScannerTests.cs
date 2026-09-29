using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

public class FillPathScannerTests
{
    [Fact]
    public void Upward_ThresholdAtOrBelowOpen_TouchesAtPositionZero()
    {
        decimal? position = FillPathScanner.TryFindTouchPosition(open: 100m, high: 110m, low: 95m, close: 105m, threshold: 100m, TouchDirection.Upward);
        Assert.Equal(0m, position);
    }

    [Fact]
    public void Upward_ThresholdWithinOpenHighLeg_TouchesPartway()
    {
        // Open=100, High=110: threshold=105 is reached 50% of the way through the Open->High leg.
        decimal? position = FillPathScanner.TryFindTouchPosition(open: 100m, high: 110m, low: 95m, close: 105m, threshold: 105m, TouchDirection.Upward);
        Assert.Equal(0.5m, position);
    }

    [Fact]
    public void Upward_ThresholdAboveHigh_NeverTouches()
    {
        decimal? position = FillPathScanner.TryFindTouchPosition(open: 100m, high: 110m, low: 95m, close: 105m, threshold: 111m, TouchDirection.Upward);
        Assert.Null(position);
    }

    [Fact]
    public void Downward_ThresholdAtOrAboveOpen_TouchesAtPositionZero()
    {
        decimal? position = FillPathScanner.TryFindTouchPosition(open: 100m, high: 110m, low: 95m, close: 105m, threshold: 100m, TouchDirection.Downward);
        Assert.Equal(0m, position);
    }

    [Fact]
    public void Downward_ThresholdWithinHighLowLeg_TouchesPartway()
    {
        // Open=105 > threshold, so it cannot touch at Open. High=110, Low=95: threshold=102.5 is
        // reached 50% of the way through the High->Low leg -> position 1.5.
        decimal? position = FillPathScanner.TryFindTouchPosition(open: 105m, high: 110m, low: 95m, close: 100m, threshold: 102.5m, TouchDirection.Downward);
        Assert.Equal(1.5m, position);
    }

    [Fact]
    public void Downward_ThresholdBelowLow_NeverTouches()
    {
        decimal? position = FillPathScanner.TryFindTouchPosition(open: 100m, high: 110m, low: 95m, close: 105m, threshold: 94m, TouchDirection.Downward);
        Assert.Null(position);
    }

    [Fact]
    public void FlatBar_NoDivideByZero_ImmediateOpenTouchOnly()
    {
        decimal? upward = FillPathScanner.TryFindTouchPosition(open: 100m, high: 100m, low: 100m, close: 100m, threshold: 100m, TouchDirection.Upward);
        decimal? downward = FillPathScanner.TryFindTouchPosition(open: 100m, high: 100m, low: 100m, close: 100m, threshold: 100m, TouchDirection.Downward);
        Assert.Equal(0m, upward);
        Assert.Equal(0m, downward);
    }

    [Fact]
    public void UpwardTouch_EarlierThanDownwardTouch_OnSameBar()
    {
        // Regardless of the specific thresholds, any Upward touch (Open->High leg) precedes any
        // Downward touch (High->Low leg) under the fixed O->H->L->C path, unless the Downward
        // condition is already true at Open.
        decimal? upward = FillPathScanner.TryFindTouchPosition(open: 100m, high: 110m, low: 95m, close: 105m, threshold: 108m, TouchDirection.Upward);
        decimal? downward = FillPathScanner.TryFindTouchPosition(open: 100m, high: 110m, low: 95m, close: 105m, threshold: 96m, TouchDirection.Downward);
        Assert.NotNull(upward);
        Assert.NotNull(downward);
        Assert.True(upward < downward);
    }
}
