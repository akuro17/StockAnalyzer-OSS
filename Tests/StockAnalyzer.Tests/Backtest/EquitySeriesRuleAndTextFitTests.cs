using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Avalonia.Views.Backtest.Rendering;
using StockAnalyzer.Core.Models.Backtest.Engine;
using Xunit;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>The shared definition of a drawable series, the binary-search partition points and the label fitting of the equity chart.</summary>
public class EquitySeriesRuleAndTextFitTests
{
    private static readonly DateTime Day0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ImmutableArray<EquityPoint> Series(params int[] dayOffsets) =>
        dayOffsets.Select((d, i) => new EquityPoint(i, Day0.AddDays(d), 1m, 1m, 0m, 0m)).ToImmutableArray();

    [Fact]
    public void Rule_AcceptsUtcStrictlyIncreasingSeriesAndReportsTheSmallestSpacing()
    {
        ImmutableArray<EquityPoint> points = Series(0, 3, 4, 10);

        Assert.True(EquitySeriesRule.TryValidate(points, out long spacing));
        Assert.Equal(TimeSpan.FromDays(1).Ticks, spacing);
        Assert.True(EquitySeriesRule.IsValid(points));
    }

    [Fact]
    public void Rule_AOnePointSeriesIsValidWithoutASpacing()
    {
        Assert.True(EquitySeriesRule.TryValidate(Series(0), out long spacing));
        Assert.Equal(0L, spacing);
    }

    [Fact]
    public void Rule_RejectsEmptyDefaultNonUtcDuplicateAndReversedSeries()
    {
        DateTime local = DateTime.SpecifyKind(Day0, DateTimeKind.Local);
        var nonUtcFirst = ImmutableArray.Create(new EquityPoint(0, local, 1m, 1m, 0m, 0m));
        var nonUtcLater = ImmutableArray.Create(
            new EquityPoint(0, Day0, 1m, 1m, 0m, 0m), new EquityPoint(1, local.AddDays(1), 1m, 1m, 0m, 0m));

        Assert.False(EquitySeriesRule.IsValid(ImmutableArray<EquityPoint>.Empty));
        Assert.False(EquitySeriesRule.IsValid(default));
        Assert.False(EquitySeriesRule.IsValid(nonUtcFirst));
        Assert.False(EquitySeriesRule.IsValid(nonUtcLater));
        Assert.False(EquitySeriesRule.IsValid(Series(0, 1, 1)));
        Assert.False(EquitySeriesRule.IsValid(Series(0, 2, 1)));
    }

    [Fact]
    public void Layout_ValidatedBuildGivesTheSameLayoutAsTheValidatingBuild()
    {
        ImmutableArray<EquityPoint> points = Series(0, 1, 2, 3, 4, 5, 6, 7);
        long start = Day0.AddDays(2).Ticks;
        long end = Day0.AddDays(5).AddHours(6).Ticks;

        EquityCurveLayout validating = EquityCurveLayout.Build(points, start, end);
        EquityCurveLayout validated = EquityCurveLayout.BuildValidated(points, start, end);

        Assert.Equal(validating.State, validated.State);
        Assert.Equal(validating.FirstIndex, validated.FirstIndex);
        Assert.Equal(validating.Points, validated.Points);
        Assert.Equal(validating.YMin, validated.YMin);
        Assert.Equal(validating.YMax, validated.YMax);
    }

    [Theory]
    [InlineData(-1, 0, 0)]       // before the first point
    [InlineData(0, 0, 1)]        // exactly the first point
    [InlineData(2, 2, 3)]        // exactly a middle point
    [InlineData(2.5, 3, 3)]      // between two points
    [InlineData(9, 5, 5)]        // after the last point (Length)
    public void PartitionPoints_AreTheFirstIndexAtOrAfterAndAfter(double day, int atOrAfter, int after)
    {
        ImmutableArray<EquityPoint> points = Series(0, 1, 2, 3, 4);
        long ticks = Day0.AddDays(day).Ticks;

        Assert.Equal(atOrAfter, EquityNearestPoint.FirstIndexAtOrAfter(points, ticks));
        Assert.Equal(after, EquityNearestPoint.FirstIndexAfter(points, ticks));
    }

    [Fact]
    public void PartitionPoints_HandleEmptyAndDefaultSeries()
    {
        Assert.Equal(0, EquityNearestPoint.FirstIndexAtOrAfter(ImmutableArray<EquityPoint>.Empty, Day0.Ticks));
        Assert.Equal(0, EquityNearestPoint.FirstIndexAfter(default, Day0.Ticks));
    }

    private static double Measure(string text) => 10d * text.Length;

    [Fact]
    public void Truncate_KeepsATextThatFits()
    {
        Assert.Equal("abc", EquityTextFit.Truncate("abc", 30d, Measure));
        Assert.Equal(string.Empty, EquityTextFit.Truncate(string.Empty, 0d, Measure));
    }

    [Fact]
    public void Truncate_CutsToTheLongestPrefixThatFitsWithTheEllipsis()
    {
        // 10 per char; the ellipsis is 3 chars = 30; width 75 leaves room for 4 characters.
        Assert.Equal("abcd" + EquityTextFit.Ellipsis, EquityTextFit.Truncate("abcdefghij", 70d, Measure));
        Assert.Equal("abcd" + EquityTextFit.Ellipsis, EquityTextFit.Truncate("abcdefghij", 75d, Measure));
        Assert.Equal("abcde" + EquityTextFit.Ellipsis, EquityTextFit.Truncate("abcdefghij", 80d, Measure));
    }

    [Fact]
    public void Truncate_ReturnsTheEllipsisAloneWhenNotEvenOneCharacterFits()
    {
        Assert.Equal(EquityTextFit.Ellipsis, EquityTextFit.Truncate("abcdefghij", 35d, Measure));
        Assert.Equal(EquityTextFit.Ellipsis, EquityTextFit.Truncate("abcdefghij", 0d, Measure));
    }

    [Fact]
    public void Truncate_NeverReturnsSomethingWiderThanTheLimitWhenAPrefixFits()
    {
        for (double limit = 40d; limit <= 120d; limit += 7d)
        {
            string result = EquityTextFit.Truncate("0123456789ABCDEF", limit, Measure);

            Assert.True(Measure(result) <= limit, $"'{result}' is wider than {limit}");
        }
    }
}
