using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests;

/// <summary>
/// A Kagi column is a run of segments that go in one direction; a flat first segment (the anchor) never shares a column with the segment after it.
/// </summary>
public class KagiColumnsTests
{
    private static readonly DateTime Start = new(2026, 1, 1);

    private static CoreCandleData Segment(int day, decimal open, decimal close)
        => new(Start.AddDays(day), open, Math.Max(open, close), Math.Min(open, close), close, 0);

    // up 100->110, up 110->120, down 120->105, down 105->95, up 95->130
    private static List<CoreCandleData> TwoSegmentColumns() => new()
    {
        Segment(0, 100m, 110m), Segment(1, 110m, 120m), Segment(2, 120m, 105m), Segment(3, 105m, 95m), Segment(4, 95m, 130m),
    };

    // flat anchor at 100, up 100->110, up 110->120, down 120->105
    private static List<CoreCandleData> WithFlatAnchor() => new()
    {
        Segment(0, 100m, 100m), Segment(1, 100m, 110m), Segment(2, 110m, 120m), Segment(3, 120m, 105m),
    };

    [Fact]
    public void StartsNewColumn_FirstSegmentNeverStartsOne()
    {
        Assert.False(KagiColumns.StartsNewColumn(TwoSegmentColumns(), 0));
    }

    [Theory]
    [InlineData(1, false)]   // same direction as the segment before
    [InlineData(2, true)]    // direction change
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void StartsNewColumn_FollowsTheDirection(int index, bool expected)
    {
        Assert.Equal(expected, KagiColumns.StartsNewColumn(TwoSegmentColumns(), index));
    }

    [Fact]
    public void StartsNewColumn_TheSegmentAfterAFlatAnchorStartsOneEvenInTheSameDirection()
    {
        var segments = WithFlatAnchor();

        Assert.True(KagiColumns.StartsNewColumn(segments, 1));    // the anchor counts as up (Close >= Open) like the segment after it
        Assert.False(KagiColumns.StartsNewColumn(segments, 2));
        Assert.True(KagiColumns.StartsNewColumn(segments, 3));
    }

    [Theory]
    [InlineData(0, new[] { 0, 0, 1, 1, 2 })]
    [InlineData(5, new[] { 5, 5, 6, 6, 7 })]
    public void BuildColumnMap_NumbersTheColumnsFromTheFirstColumn(int firstColumn, int[] expected)
    {
        var map = new List<int> { 99 };   // is cleared first

        KagiColumns.BuildColumnMap(TwoSegmentColumns(), firstColumn, map);

        Assert.Equal(expected, map);
    }

    [Fact]
    public void BuildColumnMap_PutsAFlatAnchorInItsOwnColumn()
    {
        var map = new List<int>();

        KagiColumns.BuildColumnMap(WithFlatAnchor(), 0, map);

        Assert.Equal(new[] { 0, 1, 1, 2 }, map);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(2, 2, 3)]
    [InlineData(3, 2, 3)]
    [InlineData(4, 4, 4)]
    public void ColumnBounds_ReturnTheFirstAndLastSegmentOfTheColumn(int index, int start, int end)
    {
        Assert.Equal((start, end), KagiColumns.ColumnBounds(TwoSegmentColumns(), index));
    }

    [Theory]
    [InlineData(0, 0, 0)]    // the anchor alone
    [InlineData(1, 1, 2)]
    [InlineData(2, 1, 2)]
    [InlineData(3, 3, 3)]
    public void ColumnBounds_KeepAFlatAnchorOutOfTheColumnAfterIt(int index, int start, int end)
    {
        Assert.Equal((start, end), KagiColumns.ColumnBounds(WithFlatAnchor(), index));
    }

    [Fact]
    public void ColumnBounds_AreConsistentWithBuildColumnMapForEverySegment()
    {
        var segments = new List<CoreCandleData>(WithFlatAnchor());
        segments.AddRange(TwoSegmentColumns());
        var map = new List<int>();
        KagiColumns.BuildColumnMap(segments, 0, map);

        for (int i = 0; i < segments.Count; i++)
        {
            var (start, end) = KagiColumns.ColumnBounds(segments, i);

            Assert.Equal(map[i], map[start]);
            Assert.Equal(map[i], map[end]);
            Assert.True(start == 0 || map[start - 1] != map[i]);
            Assert.True(end == segments.Count - 1 || map[end + 1] != map[i]);
        }
    }
}
