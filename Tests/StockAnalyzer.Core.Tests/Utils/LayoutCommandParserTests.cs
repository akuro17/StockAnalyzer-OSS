using System;
using System.Linq;
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

    /// <summary>Parser calls inside one measured window.</summary>
    private const int CallsPerWindow = 20_000;

    /// <summary>Measured windows; the verdict is taken from the smallest one.</summary>
    private const int WindowCount = 20;

    /// <summary>
    /// The parser allocates nothing. <see cref="GC.GetAllocatedBytesForCurrentThread"/> is not exact while other threads
    /// trigger garbage collections: a GC that runs inside the measured window adds roughly one allocation chunk
    /// (about 8 KB) to this thread's reading although the loop allocated nothing (reproduced: with 11 threads allocating,
    /// 3 to 7 of 100 windows of the former single 1,000,000-call measurement read about 8 KB, every one of them with
    /// GCs inside; the same reading was captured once in a full-suite run: 2,800 B with 6 GCs in the window). The
    /// artifact only ever ADDS to a reading, whereas a real allocation by the parser adds to EVERY window. The verdict is
    /// therefore the smallest reading of several short windows: it is 0 unless every window was disturbed, and it is
    /// above 0 whenever the parser allocates, even seldom (checked: an allocation once per 1,024 calls was detected in
    /// 60 of 60 runs under the same GC pressure).
    /// </summary>
    [Fact]
    public void TryParseCommand_WithValidInput_AllocatesZeroBytes()
    {
        const string input = "Left:Watchlist";
        var inputSpan = input.AsSpan();

        // Warm up the JIT compiler to ensure JIT allocation is excluded from the test
        LayoutCommandParser.TryParseCommand(inputSpan, out _, out _);

        var windowReadings = new long[WindowCount];
        for (int window = 0; window < WindowCount; window++)
        {
            long bytesBeforeAllocation = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < CallsPerWindow; i++)
            {
                LayoutCommandParser.TryParseCommand(inputSpan, out _, out _);
            }

            windowReadings[window] = GC.GetAllocatedBytesForCurrentThread() - bytesBeforeAllocation;
        }

        Assert.True(windowReadings.Min() == 0,
            $"The smallest of {WindowCount} windows of {CallsPerWindow} calls still read {windowReadings.Min()} B; readings: {string.Join(", ", windowReadings)}");
    }
}
