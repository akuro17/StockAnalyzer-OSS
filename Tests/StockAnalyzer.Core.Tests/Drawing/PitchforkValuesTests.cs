using System;
using System.Linq;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

public sealed class PitchforkValuesTests
{
    private static readonly DateTime T1 = new(2026, 1, 1);
    private static readonly DrawingAnchor P1 = new(T1, 100m);
    private static readonly DrawingAnchor P2 = new(T1.AddDays(4), 160m);
    private static readonly DrawingAnchor P3 = new(T1.AddDays(8), 140m);

    [Fact]
    public void Anchor_OfEachKind()
    {
        Assert.Equal(P1, PitchforkValues.GetAnchor(PitchforkKind.Andrews, P1, P2));
        Assert.Equal(new DrawingAnchor(T1, 130m), PitchforkValues.GetAnchor(PitchforkKind.Schiff, P1, P2));
        // Minute-aligned times: half the calendar seconds equals half the ticks.
        Assert.Equal(new DrawingAnchor(T1 + (P2.Time - P1.Time) / 2, 130m), PitchforkValues.GetAnchor(PitchforkKind.ModifiedSchiff, P1, P2));
        Assert.Equal(T1.AddDays(2), PitchforkValues.GetAnchor(PitchforkKind.ModifiedSchiff, P1, P2).Time);
    }

    [Fact]
    public void MedianPoint_IsTheMidpointOfP2AndP3()
    {
        Assert.Equal(new DrawingAnchor(T1.AddDays(6), 150m), PitchforkValues.GetMedianPoint(P2, P3));
    }

    [Theory]
    [InlineData(PitchforkKind.Andrews, 6, 150, 160, 140)]
    [InlineData(PitchforkKind.Schiff, 6, 150, 160, 140)]
    [InlineData(PitchforkKind.ModifiedSchiff, 6, 150, 160, 140)]
    [InlineData(PitchforkKind.Andrews, 0, 100, 110, 90)]
    [InlineData(PitchforkKind.Schiff, 0, 130, 140, 120)]
    [InlineData(PitchforkKind.ModifiedSchiff, 2, 130, 140, 120)]
    public void Calculate_LinesAtTheTimestamp(PitchforkKind kind, int dayOffset, decimal median, decimal upper, decimal lower)
    {
        var values = PitchforkValues.Calculate(kind, P1, P2, P3, T1.AddDays(dayOffset), IndicatorColor.Gray);

        Assert.Equal(new[] { "UpperLine", "MedianLine", "LowerLine", "ForkType" }, values.Select(v => v.Key).ToArray());
        Assert.Equal(upper, values[0].NumericValue);
        Assert.Equal(median, values[1].NumericValue);
        Assert.Equal(lower, values[2].NumericValue);
        Assert.Equal(kind.ToString(), values[3].FormattedText);
        Assert.Null(values[3].NumericValue);
    }

    [Fact]
    public void Calculate_TextUsesTheFixedCulture()
    {
        var values = PitchforkValues.Calculate(PitchforkKind.Andrews, P1, P2, P3, T1.AddDays(6), IndicatorColor.Gray);

        Assert.Equal("150.000", values[1].FormattedText);
    }
}
