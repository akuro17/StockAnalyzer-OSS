using System;
using System.Collections.Generic;
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
    // The outer lines are the lines through P2 and P3 parallel to the median line (WebAiPeerReview plan, owner decision D-1 = A, 2026-11-16).
    // Hand values: P1 (0 d, 100), P2 (4 d, 160), P3 (8 d, 140), median point (6 d, 150). The prices are not exact decimals, so they are compared to 9 digits.
    [InlineData(PitchforkKind.Andrews, 6, 150.0, 176.666666667, 123.333333333)]
    [InlineData(PitchforkKind.Schiff, 6, 150.0, 166.666666667, 133.333333333)]
    [InlineData(PitchforkKind.ModifiedSchiff, 6, 150.0, 170.0, 130.0)]
    [InlineData(PitchforkKind.Andrews, 0, 100.0, 126.666666667, 73.333333333)]
    [InlineData(PitchforkKind.Andrews, 4, 133.333333333, 160.0, 106.666666667)]
    [InlineData(PitchforkKind.Schiff, 0, 130.0, 146.666666667, 113.333333333)]
    [InlineData(PitchforkKind.ModifiedSchiff, 2, 130.0, 150.0, 110.0)]
    public void Calculate_LinesAtTheTimestamp(PitchforkKind kind, int dayOffset, double median, double upper, double lower)
    {
        var values = PitchforkValues.Calculate(kind, P1, P2, P3, T1.AddDays(dayOffset), IndicatorColor.Gray);

        Assert.Equal(new[] { "UpperLine", "MedianLine", "LowerLine", "ForkType" }, values.Select(v => v.Key).ToArray());
        Assert.Equal((decimal)upper, values[0].NumericValue!.Value, 9);
        Assert.Equal((decimal)median, values[1].NumericValue!.Value, 9);
        Assert.Equal((decimal)lower, values[2].NumericValue!.Value, 9);
        Assert.Equal(kind.ToString(), values[3].FormattedText);
        Assert.Null(values[3].NumericValue);
    }

    [Theory]
    [InlineData(PitchforkKind.Andrews)]
    [InlineData(PitchforkKind.Schiff)]
    [InlineData(PitchforkKind.ModifiedSchiff)]
    public void Calculate_TheLineThroughP2PassesThroughP2_AndTheLineThroughP3ThroughP3(PitchforkKind kind)
    {
        // P2 is above its median-line value here (Upper passes through P2) and P3 is below (Lower passes through P3).
        decimal atP2 = Upper(PitchforkValues.Calculate(kind, P1, P2, P3, P2.Time, IndicatorColor.Gray));
        decimal atP3 = Lower(PitchforkValues.Calculate(kind, P1, P2, P3, P3.Time, IndicatorColor.Gray));

        Assert.Equal(P2.Price, atP2, 9);
        Assert.Equal(P3.Price, atP3, 9);
    }

    [Theory]
    [InlineData(PitchforkKind.Andrews)]
    [InlineData(PitchforkKind.Schiff)]
    [InlineData(PitchforkKind.ModifiedSchiff)]
    public void Calculate_WhenP2IsOnTheLowerTine_UpperStaysAboveLower_AndEachLinePassesThroughItsControlPoint(PitchforkKind kind)
    {
        // Clicked the other way round: P2 (4 d, 120) is below the median line, P3 (8 d, 180) above it. Same median point (6 d, 150).
        var p2 = new DrawingAnchor(T1.AddDays(4), 120m);
        var p3 = new DrawingAnchor(T1.AddDays(8), 180m);

        for (int day = -3; day <= 14; day++)
        {
            var values = PitchforkValues.Calculate(kind, P1, p2, p3, T1.AddDays(day), IndicatorColor.Gray);
            decimal upper = Upper(values), median = Median(values), lower = Lower(values);

            Assert.True(upper >= lower, $"day {day}: upper {upper} < lower {lower}");
            Assert.Equal(upper - median, median - lower, 6);
        }

        var atP2 = PitchforkValues.Calculate(kind, P1, p2, p3, p2.Time, IndicatorColor.Gray);
        var atP3 = PitchforkValues.Calculate(kind, P1, p2, p3, p3.Time, IndicatorColor.Gray);
        Assert.Equal(p2.Price, Lower(atP2), 9);   // P2 lies below the median line: its line is the lower one
        Assert.Equal(p3.Price, Upper(atP3), 9);
    }

    [Fact]
    public void Calculate_WhenP2IsOnTheLowerTine_MatchesTheHandValues()
    {
        var p2 = new DrawingAnchor(T1.AddDays(4), 120m);
        var p3 = new DrawingAnchor(T1.AddDays(8), 180m);

        var d4 = PitchforkValues.Calculate(PitchforkKind.Andrews, P1, p2, p3, T1.AddDays(4), IndicatorColor.Gray);
        var d6 = PitchforkValues.Calculate(PitchforkKind.Andrews, P1, p2, p3, T1.AddDays(6), IndicatorColor.Gray);
        var d8 = PitchforkValues.Calculate(PitchforkKind.Andrews, P1, p2, p3, T1.AddDays(8), IndicatorColor.Gray);

        Assert.Equal(133.333333333m, Median(d4), 9);
        Assert.Equal(146.666666667m, Upper(d4), 9);
        Assert.Equal(120m, Lower(d4), 9);
        Assert.Equal(150m, Median(d6), 9);
        Assert.Equal(163.333333333m, Upper(d6), 9);
        Assert.Equal(136.666666667m, Lower(d6), 9);
        Assert.Equal(166.666666667m, Median(d8), 9);
        Assert.Equal(180m, Upper(d8), 9);
        Assert.Equal(153.333333333m, Lower(d8), 9);
    }

    [Fact]
    public void Calculate_WhenP2AndP3ShareATime_EqualsTheOldFormula()
    {
        // At one time both offsets are measured at the median point's own time, so the result is median +- (p - pM).
        var p2 = new DrawingAnchor(T1.AddDays(6), 170m);
        var p3 = new DrawingAnchor(T1.AddDays(6), 130m);

        var values = PitchforkValues.Calculate(PitchforkKind.Andrews, P1, p2, p3, T1.AddDays(3), IndicatorColor.Gray);

        Assert.Equal(125m, Median(values), 9);       // line from (0 d, 100) through the median point (6 d, 150)
        Assert.Equal(145m, Upper(values), 9);        // + (170 - 150)
        Assert.Equal(105m, Lower(values), 9);        // + (130 - 150)
    }

    [Fact]
    public void Calculate_WhenTheMedianLineIsFlat_TheOuterLinesAreP2AndP3Prices()
    {
        // The anchor and the median point share a time: InterpolatePrice returns the anchor price, the median line is horizontal at 150.
        var p1 = new DrawingAnchor(T1.AddDays(6), 150m);

        foreach (int day in new[] { 0, 6, 12 })
        {
            var values = PitchforkValues.Calculate(PitchforkKind.Andrews, p1, P2, P3, T1.AddDays(day), IndicatorColor.Gray);
            Assert.Equal(150m, Median(values), 9);
            Assert.Equal(160m, Upper(values), 9);
            Assert.Equal(140m, Lower(values), 9);
        }
    }

    private static decimal Upper(IReadOnlyList<DrawingCalculatedValue> values) => values.Single(v => v.Key == "UpperLine").NumericValue!.Value;

    private static decimal Median(IReadOnlyList<DrawingCalculatedValue> values) => values.Single(v => v.Key == "MedianLine").NumericValue!.Value;

    private static decimal Lower(IReadOnlyList<DrawingCalculatedValue> values) => values.Single(v => v.Key == "LowerLine").NumericValue!.Value;

    [Fact]
    public void Calculate_TextUsesTheFixedCulture()
    {
        var values = PitchforkValues.Calculate(PitchforkKind.Andrews, P1, P2, P3, T1.AddDays(6), IndicatorColor.Gray);

        Assert.Equal("150.000", values[1].FormattedText);
    }
}
