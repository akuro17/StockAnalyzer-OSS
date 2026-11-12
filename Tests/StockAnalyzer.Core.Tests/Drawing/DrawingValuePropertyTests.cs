using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

/// <summary>Properties of the drawing-value math that hold for the current definitions (pins, so a later change of a definition is seen).</summary>
public sealed class DrawingValuePropertyTests
{
    private static readonly DateTime T1 = new(2026, 1, 1);

    private static DrawingCalculatedValue Value(IReadOnlyList<DrawingCalculatedValue> values, string key) => values.Single(v => v.Key == key);

    // ---- thresholds (seconds for the line, days for the slope) ----

    [Theory]
    [InlineData(0L)]
    [InlineData(500L)]       // 0.00005 s
    public void InterpolatePrice_ForTimesCloserThanTheSecondsThreshold_ReturnsTheFirstPrice(long ticks)
    {
        decimal price = DrawingValueMath.InterpolatePrice(T1, 100m, T1.AddTicks(ticks), 190m, T1.AddTicks(ticks));

        Assert.Equal(100m, price);
    }

    [Theory]
    [InlineData(5_000_000L)]    // 0.5 s
    [InlineData(10_000_000L)]   // 1 s
    public void InterpolatePrice_ForTimesFartherApartThanTheSecondsThreshold_InterpolatesToTheSecondPriceAtTheSecondTime(long ticks)
    {
        decimal price = DrawingValueMath.InterpolatePrice(T1, 100m, T1.AddTicks(ticks), 190m, T1.AddTicks(ticks));

        Assert.Equal(190m, price);
    }

    [Theory]
    [InlineData(5_000_000L)]    // 0.5 s
    [InlineData(10_000_000L)]   // 1 s
    [InlineData(80_000_000L)]   // 8 s, under 8.64 s (1e-4 days)
    public void CalculateSlopePerDay_ForTimesCloserThanTheDaysThreshold_IsNull(long ticks)
    {
        Assert.Null(DrawingValueMath.CalculateSlopePerDay(T1, 100m, T1.AddTicks(ticks), 190m));
    }

    [Fact]
    public void CalculateSlopePerDay_AboveTheDaysThreshold_HasAValue()
    {
        decimal? nineSeconds = DrawingValueMath.CalculateSlopePerDay(T1, 100m, T1.AddSeconds(9), 190m);
        decimal? oneDay = DrawingValueMath.CalculateSlopePerDay(T1, 100m, T1.AddDays(1), 190m);

        Assert.NotNull(nineSeconds);
        Assert.True(Math.Abs(nineSeconds.Value - 864000m) < 0.001m, nineSeconds.Value.ToString());
        Assert.Equal(90m, oneDay);
    }

    // ---- order of the two points ----

    [Fact]
    public void SwappingTheTwoPoints_KeepsTheLine_AndTheSlope()
    {
        DateTime t2 = T1.AddDays(1);
        DateTime between = T1.AddHours(12);

        decimal forward = DrawingValueMath.InterpolatePrice(T1, 100m, t2, 110m, between);
        decimal swapped = DrawingValueMath.InterpolatePrice(t2, 110m, T1, 100m, between);

        Assert.True(Math.Abs(forward - swapped) <= 0.000000001m, $"{forward} vs {swapped}");
        Assert.Equal(DrawingValueMath.CalculateSlopePerDay(T1, 100m, t2, 110m), DrawingValueMath.CalculateSlopePerDay(t2, 110m, T1, 100m));
    }

    // ---- SineLine ----

    [Fact]
    public void SineLine_IsTheCentreAtTheStart_ThePeakAfterAQuarterPeriod_AndBackAtTheCentreAtTheSecondPoint()
    {
        var p1 = new DrawingAnchor(T1, 100m);
        var p2 = new DrawingAnchor(T1.AddDays(10), 120m);
        // F3 text: sin(pi) and sin(2 pi) are not exactly zero in double.
        (double Days, string Text)[] expected = { (0, "100.000"), (2.5, "120.000"), (5, "100.000"), (7.5, "80.000"), (10, "100.000") };

        foreach ((double days, string text) in expected)
        {
            var values = SineLineValues.Calculate(p1, p2, T1.AddDays(days), IndicatorColor.Gray);

            Assert.Equal(text, Value(values, "SinePrice").FormattedText);
        }
    }

    // ---- period limit of the period tools (DrawingValueMath.MinPeriodSeconds, one second) ----

    [Theory]
    [InlineData(5_000_000L)]    // 0.5 s
    [InlineData(9_999_999L)]    // one tick under 1 s
    public void SineLineAndTimeCycles_ForAPeriodUnderOneSecond_GiveNoValues(long ticks)
    {
        var p1 = new DrawingAnchor(T1, 100m);
        var p2 = new DrawingAnchor(T1.AddTicks(ticks), 120m);

        Assert.Empty(SineLineValues.Calculate(p1, p2, T1, IndicatorColor.Gray));
        Assert.Empty(TimeCyclesValues.Calculate(p1, p2, T1, IndicatorColor.Gray));
    }

    [Theory]
    [InlineData(10_000_000L)]   // exactly 1 s
    [InlineData(20_000_000L)]   // 2 s
    public void SineLineAndTimeCycles_ForAPeriodOfAtLeastOneSecond_GiveValues(long ticks)
    {
        var p1 = new DrawingAnchor(T1, 100m);
        var p2 = new DrawingAnchor(T1.AddTicks(ticks), 120m);

        Assert.Equal(4, SineLineValues.Calculate(p1, p2, T1, IndicatorColor.Gray).Count);
        Assert.Equal(3, TimeCyclesValues.Calculate(p1, p2, T1, IndicatorColor.Gray).Count);
    }

    // ---- level tables ----

    [Fact]
    public void FibonacciRetracementTables_AreExactlyTheDecimalLiterals()
    {
        Assert.Equal(new[] { 0m, 0.236m, 0.382m, 0.5m, 0.618m, 0.786m, 1m }, FibonacciRetracementValues.DefaultLevels.ToArray().Select(l => (decimal)l).ToArray());
        Assert.Equal(
            new[] { -3.236m, -1.618m, -0.618m, -0.272m, 0m, 0.236m, 0.382m, 0.5m, 0.618m, 0.786m, 1m, 1.272m, 1.618m, 2.618m, 4.236m },
            FibonacciRetracementValues.ExtensionLevels.ToArray().Select(l => (decimal)l).ToArray());
    }

    [Fact]
    public void FibonacciExpansionChannelAndFanTables_AreExactlyTheDecimalLiterals()
    {
        Assert.Equal(new[] { 0.618m, 1m, 1.618m, 2.618m, 4.236m }, FibonacciExpansionValues.Levels.ToArray().Select(l => (decimal)l).ToArray());
        Assert.Equal(new[] { 0m, 0.618m, 1m, 1.618m, 2.618m }, FibonacciChannelValues.Levels.ToArray().Select(l => (decimal)l).ToArray());
        Assert.Equal(new[] { 0.382m, 0.5m, 0.618m, 1m }, FibonacciFanValues.Ratios.ToArray().Select(r => (decimal)r).ToArray());
    }

    [Fact]
    public void FibonacciExpansion_LevelOne_IsTheThirdPricePlusTheImpulse()
    {
        var values = FibonacciExpansionValues.Calculate(new DrawingAnchor(T1, 100m), new DrawingAnchor(T1.AddDays(10), 150m), new DrawingAnchor(T1.AddDays(7.5), 130m), IndicatorColor.Gray);

        Assert.Equal(180m, Value(values, "Exp_1.000").NumericValue);
    }

    [Fact]
    public void GannBoxRatios_AreMultiplesOfOneEighth_SoTheKeyNamesThePosition()
    {
        var values = GannBoxValues.Calculate(new DrawingAnchor(T1, 100m), new DrawingAnchor(T1.AddDays(10), 180m), IndicatorColor.Gray);

        foreach (double ratio in GannBoxValues.GridRatios.ToArray())
        {
            double eighths = ratio * 8.0;
            Assert.True(Math.Abs(eighths - Math.Round(eighths)) < 1e-12, $"{ratio} is not a multiple of 1/8.");
            Assert.Equal(100m + 80m * (decimal)ratio, Value(values, $"Gann_{(int)Math.Round(eighths)}_8").NumericValue);
        }
    }

    // ---- extreme times ----

    [Fact]
    public void EveryTool_AtTheSmallestAndLargestDateTime_DoesNotThrow()
    {
        var anchors = new[]
        {
            new DrawingAnchor(T1, 100m),
            new DrawingAnchor(T1.AddDays(10), 150m),
            new DrawingAnchor(T1.AddDays(7.5), 130m),
        };

        foreach (DrawingValueDescriptor descriptor in DrawingValueCatalog.All)
        {
            foreach (DateTime time in new[] { DateTime.MinValue, DateTime.MaxValue })
            {
                var request = new DrawingValueRequest(anchors.Take(descriptor.PointCount).ToArray(), time, 123.456m, IndicatorColor.Gray, new DrawingValueOptions(true));

                Exception? thrown = Record.Exception(() => descriptor.Calculate(request));

                Assert.True(thrown is null, $"{descriptor.Name} at {time:O}: {thrown}");
            }
        }
    }
}
