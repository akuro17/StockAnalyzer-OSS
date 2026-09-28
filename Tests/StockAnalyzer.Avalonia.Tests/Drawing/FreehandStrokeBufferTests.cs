using System;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class FreehandStrokeBufferTests
{
    [Fact]
    public void TryAppend_AddsPointsSequentially()
    {
        var buffer = new FreehandStrokeBuffer(10);
        var p1 = new ChartPoint(DateTime.UtcNow, 100m);
        var p2 = new ChartPoint(DateTime.UtcNow.AddMinutes(1), 105m);

        Assert.True(buffer.TryAppend(p1));
        Assert.True(buffer.TryAppend(p2));

        Assert.Equal(2, buffer.Count);
        Assert.Equal(p1.Time, buffer[0].Time);
        Assert.Equal(p1.Price, buffer[0].Price);
        Assert.Equal(p2.Time, buffer[1].Time);
        Assert.Equal(p2.Price, buffer[1].Price);
    }

    [Fact]
    public void TryAppend_ReachesCapacity_ReturnsFalse()
    {
        var buffer = new FreehandStrokeBuffer(3);
        var p = new ChartPoint(DateTime.UtcNow, 100m);

        Assert.True(buffer.TryAppend(p));
        Assert.True(buffer.TryAppend(p));
        Assert.True(buffer.TryAppend(p));
        Assert.False(buffer.TryAppend(p));

        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Clear_ResetsCountToZero()
    {
        var buffer = new FreehandStrokeBuffer(10);
        buffer.TryAppend(new ChartPoint(DateTime.UtcNow, 100m));
        buffer.TryAppend(new ChartPoint(DateTime.UtcNow, 101m));
        Assert.Equal(2, buffer.Count);

        buffer.Clear();
        Assert.Equal(0, buffer.Count);
        Assert.Empty(buffer.AsSpan().ToArray());
    }

    [Fact]
    public void AsSpan_ReturnsExactSlice()
    {
        var buffer = new FreehandStrokeBuffer(5);
        var p1 = new ChartPoint(DateTime.UtcNow, 100m);
        var p2 = new ChartPoint(DateTime.UtcNow.AddSeconds(10), 102m);

        buffer.TryAppend(p1);
        buffer.TryAppend(p2);

        var span = buffer.AsSpan();
        Assert.Equal(2, span.Length);
        Assert.Equal(p1.Price, span[0].Price);
        Assert.Equal(p2.Price, span[1].Price);
    }

    [Fact]
    public void Constructor_InvalidCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FreehandStrokeBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FreehandStrokeBuffer(-5));
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        var buffer = new FreehandStrokeBuffer(5);
        buffer.TryAppend(new ChartPoint(DateTime.UtcNow, 100m));

        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[-1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer[1]);
    }
}
