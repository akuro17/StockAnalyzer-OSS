using System;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Regression test: right after placing a Long/Short Position tool, the initial Stop/Target
/// offsets from Entry were too small (1%/2%) to comfortably grab and drag. Fixed to a symmetric
/// +/-10% of Entry price.
/// </summary>
public class LongShortPositionInitialSizeTests
{
    [Fact]
    public void LongPosition_InitialPlacement_StopAndTargetAreTenPercentFromEntry()
    {
        var behavior = new LongShortPositionBehavior(isLong: true);
        var entry = new ChartPoint(new DateTime(2024, 1, 1), 100m);

        var obj = Assert.IsType<LongShortPositionObject>(behavior.CreateObject(entry));

        Assert.Equal(100m, obj.Points[0].Price); // Entry
        Assert.Equal(90m, obj.Points[1].Price);  // Stop: Entry - 10%
        Assert.Equal(110m, obj.Points[2].Price); // Target: Entry + 10%
    }

    [Fact]
    public void ShortPosition_InitialPlacement_StopAndTargetAreTenPercentFromEntry()
    {
        var behavior = new LongShortPositionBehavior(isLong: false);
        var entry = new ChartPoint(new DateTime(2024, 1, 1), 100m);

        var obj = Assert.IsType<LongShortPositionObject>(behavior.CreateObject(entry));

        Assert.Equal(100m, obj.Points[0].Price); // Entry
        Assert.Equal(110m, obj.Points[1].Price); // Stop: Entry + 10%
        Assert.Equal(90m, obj.Points[2].Price);  // Target: Entry - 10%
    }
}
