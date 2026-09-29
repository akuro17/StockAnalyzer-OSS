#nullable enable
using StockAnalyzer.Core.Services.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Core.Tests.Backtest;

/// <summary>
/// T7b (A1 (lifetime and reversal chronology)): <see cref="FillPathScanner.TryFindTouchFrom"/> scans the residual path
/// from an arbitrary start point (inclusive); from the Open it must reproduce <see cref="FillPathScanner.TryFindTouchPosition"/> exactly.
/// </summary>
public class FillPathScannerTouchFromTests
{
    // Bar O100 H110 L90 C105: legs O->H (rising, positions 0..1), H->L (falling, 1..2), L->C (rising, 2..3).
    private const decimal Open = 100m, High = 110m, Low = 90m, Close = 105m;

    private static PathPoint? From(decimal threshold, TouchDirection direction, PathPoint start)
        => FillPathScanner.TryFindTouchFrom(Open, High, Low, Close, threshold, direction, start);

    [Fact]
    public void FromOpen_ReproducesTryFindTouchPosition_OverAnExhaustiveGridWithNonTerminatingQuotients()
    {
        decimal[] grid = { 80m, 85m, 90.3m, 95m, 100m, 107.7m, 115m, 120.1m };
        int compared = 0;

        foreach (decimal low in grid)
        foreach (decimal high in grid)
        {
            if (high < low) continue;
            foreach (decimal open in grid)
            foreach (decimal close in grid)
            {
                if (open < low || open > high || close < low || close > high) continue;
                foreach (decimal threshold in grid)
                foreach (TouchDirection direction in new[] { TouchDirection.Upward, TouchDirection.Downward })
                {
                    decimal? legacy = FillPathScanner.TryFindTouchPosition(open, high, low, close, threshold, direction);
                    PathPoint? scanned = FillPathScanner.TryFindTouchFrom(open, high, low, close, threshold, direction, FillPathScanner.OpenPoint(open));

                    Assert.Equal(legacy, scanned?.Position);
                    if (scanned is { } point)
                    {
                        Assert.Equal(point.Position == 0m ? open : threshold, point.Price);
                    }
                    compared++;
                }
            }
        }

        Assert.True(compared > 5_000, $"grid unexpectedly small: {compared}");
    }

    [Fact]
    public void FromMidRisingLeg_DownwardTouchIsFoundOnTheFallingLeg()
    {
        // Start on Open->High at position 0.5 (price 105); 95 is reached on High->Low at 1 + (110 - 95) / (110 - 90).
        PathPoint? touch = From(95m, TouchDirection.Downward, new PathPoint(0.5m, 105m));

        Assert.Equal(new PathPoint(1.75m, 95m), touch);
    }

    [Fact]
    public void ConditionAlreadyHoldingAtTheStart_TouchesAtTheStartWithTheStartPrice()
    {
        PathPoint start = new(1m, 110m);

        Assert.Equal(start, From(112m, TouchDirection.Downward, start)); // 110 <= 112 already
        Assert.Equal(start, From(108m, TouchDirection.Upward, start));   // 110 >= 108 already
    }

    [Fact]
    public void ResidualPathOnly_AnEarlierExtremumIsNeverReused()
    {
        // 95 is crossed downward on High->Low (position 1.75). Starting at the Low (position 2) - after that crossing - the earlier crossing is
        // never reused: the start itself holds (90 <= 95), and a threshold below the Low is never reached because Low->Close only rises.
        Assert.Equal(new PathPoint(2m, 90m), From(95m, TouchDirection.Downward, new PathPoint(2m, 90m))); // the start itself holds (90 <= 95)
        Assert.Null(From(89m, TouchDirection.Downward, new PathPoint(2m, 90m)));                          // Low is 90: never at/below 89
    }

    [Fact]
    public void LowToCloseLeg_UpwardTouchIsFoundOnlyAfterTheLow()
    {
        // From the Low (2, 90): 100 is reached on Low->Close (90 -> 105) at 2 + (100 - 90) / (105 - 90).
        Assert.Equal(new PathPoint(2m + (10m / 15m), 100m), From(100m, TouchDirection.Upward, new PathPoint(2m, 90m)));
        Assert.Null(From(106m, TouchDirection.Upward, new PathPoint(2m, 90m))); // Close 105 never reaches 106 (the earlier High does not count)
    }

    [Fact]
    public void StartAtClose_OnlyTheClosePriceItselfCanTouch()
    {
        PathPoint closePoint = FillPathScanner.ClosePoint(Close);

        Assert.Equal(closePoint, From(110m, TouchDirection.Downward, closePoint));
        Assert.Null(From(100m, TouchDirection.Downward, closePoint));
        Assert.Equal(closePoint, From(100m, TouchDirection.Upward, closePoint));
    }
}
