using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Views.Notes;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Notes;

/// <summary>
/// <see cref="SelectionOffsetMapper"/> against the layout pinned by NoteCardView_SelectionProbeTests: "ab " + [#tag button] + " cd"
/// = a=0 b=1 space=2 container=3 space=4 c=5 d=6, end=7.
/// </summary>
public class SelectionOffsetMapperTests
{
    private static readonly IReadOnlyList<BodyLayoutPiece> HashtagBody = new[]
    {
        new BodyLayoutPiece(0, 3, "ab ", false),
        new BodyLayoutPiece(3, 1, "#tag", true),
        new BodyLayoutPiece(4, 3, " cd", false),
    };

    [Theory]
    [InlineData(0, 3, "ab ")]
    [InlineData(0, 4, "ab #tag")]
    [InlineData(3, 4, "#tag")]
    [InlineData(4, 7, " cd")]
    [InlineData(0, 5, "ab #tag ")]
    [InlineData(2, 8, " #tag cd")]
    [InlineData(0, 7, "ab #tag cd")]
    [InlineData(1, 2, "b")]
    [InlineData(5, 7, "cd")]
    public void Build_ReturnsTheDrawnTextOfTheRange(int start, int end, string expected)
    {
        Assert.Equal(expected, SelectionOffsetMapper.Build(HashtagBody, start, end));
    }

    [Fact]
    public void Build_ReversedRange_IsTheSameAsTheForwardRange()
    {
        Assert.Equal("ab #tag ", SelectionOffsetMapper.Build(HashtagBody, 5, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    [InlineData(7, 7)]
    public void Build_EmptyRange_IsEmpty(int start, int end)
    {
        Assert.Equal(string.Empty, SelectionOffsetMapper.Build(HashtagBody, start, end));
    }

    [Fact]
    public void Build_RangeOutsideThePieces_IsClamped()
    {
        Assert.Equal("ab #tag cd", SelectionOffsetMapper.Build(HashtagBody, -5, 100));
        Assert.Equal(string.Empty, SelectionOffsetMapper.Build(HashtagBody, 50, 60));
    }

    [Fact]
    public void Build_NoPieces_IsEmpty()
    {
        Assert.Equal(string.Empty, SelectionOffsetMapper.Build(Array.Empty<BodyLayoutPiece>(), 0, 10));
    }

    [Fact]
    public void Build_ImageContainerDrawsNoText_AndZeroLengthRunsAreIgnored()
    {
        var pieces = new[]
        {
            new BodyLayoutPiece(0, 2, "ab", false),
            new BodyLayoutPiece(2, 1, string.Empty, true),
            new BodyLayoutPiece(3, 0, string.Empty, false),
            new BodyLayoutPiece(3, 2, "cd", false),
        };

        Assert.Equal("abcd", SelectionOffsetMapper.Build(pieces, 0, 5));
        Assert.Equal(string.Empty, SelectionOffsetMapper.Build(pieces, 2, 3));
    }

    [Fact]
    public void Build_NewlinesInsideARunAreKeptAsTyped()
    {
        var pieces = new[] { new BodyLayoutPiece(0, 11, "line1\r\nline", false), new BodyLayoutPiece(11, 1, "2", false) };

        Assert.Equal("1\r\nli", SelectionOffsetMapper.Build(pieces, 4, 9));
    }

    [Fact]
    public void TotalLength_IsTheEndOfTheLastPiece()
    {
        Assert.Equal(7, SelectionOffsetMapper.TotalLength(HashtagBody));
        Assert.Equal(0, SelectionOffsetMapper.TotalLength(Array.Empty<BodyLayoutPiece>()));
    }
}
