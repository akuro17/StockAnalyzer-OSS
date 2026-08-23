using System;
using Xunit;
using StockAnalyzer.Core.Models.UI;
using StockAnalyzer.Core.Utils;

namespace StockAnalyzer.Core.Tests.Utils;

/// <summary>
/// Unit and performance test suite for LayoutCommandParser.
/// Ensures all boundary constraints, exploits, and memory contracts are fully validated.
/// </summary>
public class LayoutCommandParserTests
{
    [Theory]
    [InlineData("Left:Watchlist", PanelRegion.Left, "Watchlist")]
    [InlineData("RIGHT:TickerList", PanelRegion.Right, "TickerList")]
    [InlineData("Bottom:ChartPanel", PanelRegion.Bottom, "ChartPanel")]
    [InlineData("top:TabId-123", PanelRegion.Top, "TabId-123")]
    public void TryParseCommand_WithValidFormat_ReturnsTrueAndSetsParams(
        string input, PanelRegion expectedRegion, string expectedId)
    {
        var result = LayoutCommandParser.TryParseCommand(input.AsSpan(), out var actualRegion, out var actualId);
        Assert.True(result);
        Assert.Equal(expectedRegion, actualRegion);
        Assert.True(actualId.SequenceEqual(expectedId.AsSpan()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Left")]
    [InlineData(":Watchlist")]
    [InlineData("Left:")]
    [InlineData(" Left:Watchlist")]
    [InlineData("Left: Watchlist")]
    [InlineData("Left :Watchlist")]
    [InlineData("Left:Watchlist ")]
    [InlineData("99:Watchlist")]
    [InlineData("-1:Watchlist")]
    [InlineData("InvalidRegion:Watchlist")]
    [InlineData("Left:Watchlist:Extra")]
    public void TryParseCommand_WithInvalidFormat_ReturnsFalseAndDefault(string input)
    {
        var result = LayoutCommandParser.TryParseCommand(input.AsSpan(), out var actualRegion, out var actualId);
        Assert.False(result);
        Assert.Equal(PanelRegion.Unknown, actualRegion);
        Assert.True(actualId.IsEmpty);
    }

    [Fact]
    public void TryParseCommand_OnlyWhiteSpaceId_ShouldFail()
    {
        var result = LayoutCommandParser.TryParseCommand("Left:   ".AsSpan(), out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryParseCommand_WithValidInput_AllocatesZeroBytes()
    {
        const string input = "Left:Watchlist";
        var inputSpan = input.AsSpan();

        // Warm up the JIT compiler to ensure JIT allocation is excluded from the test
        LayoutCommandParser.TryParseCommand(inputSpan, out _, out _);

        long bytesBeforeAllocation = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 1_000_000; i++)
        {
            LayoutCommandParser.TryParseCommand(inputSpan, out _, out _);
        }

        long bytesAfterAllocation = GC.GetAllocatedBytesForCurrentThread();
        long allocationDelta = bytesAfterAllocation - bytesBeforeAllocation;

        Assert.Equal(0, allocationDelta);
    }
}
