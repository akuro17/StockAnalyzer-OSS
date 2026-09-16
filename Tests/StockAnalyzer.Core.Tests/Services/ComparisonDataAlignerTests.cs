using Microsoft.Extensions.Logging;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public class ComparisonDataAlignerTests
{
    private readonly Mock<IDataService> _dataServiceMock;
    private readonly ComparisonDataAligner _aligner;

    public ComparisonDataAlignerTests()
    {
        _dataServiceMock = new Mock<IDataService>();
        _aligner = new ComparisonDataAligner(_dataServiceMock.Object);
    }

    [Fact]
    public async Task AlignAsync_PerfectMatch_ReturnsMatchingSeries()
    {
        // Arrange
        var ts1 = new DateTime(2024, 1, 1);
        var ts2 = new DateTime(2024, 1, 2);
        var ts3 = new DateTime(2024, 1, 3);
        var timestamps = new[] { ts1, ts2, ts3 };

        var primaryData = timestamps.Select(ts => new CandleData(ts, 100, 110, 90, 105, 1000)).ToList();
        var compData = timestamps.Select(ts => new CandleData(ts, 200, 210, 190, 205, 2000)).ToList();

        _dataServiceMock.Setup(d => d.LoadCandlesAsync("AAPL", TimeFrame.D1, 3))
            .ReturnsAsync(primaryData);
        _dataServiceMock.Setup(d => d.LoadCandlesAsync("MSFT", TimeFrame.D1, 3))
            .ReturnsAsync(compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.D1, 3);

        // Assert
        Assert.Equal("AAPL", result.PrimarySymbol);
        Assert.Equal(2, result.Series.Count); // MSFT + AAPL
        Assert.Equal(3, result.Series["MSFT"].Length);
        Assert.All(result.Series["MSFT"], c => Assert.NotNull(c));
        Assert.Equal(205m, result.Series["MSFT"][2]!.Value.Close);
    }

    [Fact]
    public async Task AlignAsync_GapWithLOCF_FillsMissingPrice()
    {
        // Arrange
        var ts1 = new DateTime(2024, 1, 1);
        var ts2 = new DateTime(2024, 1, 2);
        var ts3 = new DateTime(2024, 1, 3);
        
        var primaryData = new List<CandleData> {
            new(ts1, 10, 10, 10, 10, 100),
            new(ts2, 11, 11, 11, 11, 100),
            new(ts3, 12, 12, 12, 12, 100)
        };
        
        // MSFT is missing ts2
        var compData = new List<CandleData> {
            new(ts1, 100, 100, 100, 105, 1000),
            new(ts3, 110, 110, 110, 115, 1000)
        };

        _dataServiceMock.Setup(d => d.LoadCandlesAsync("AAPL", It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ReturnsAsync(primaryData);
        _dataServiceMock.Setup(d => d.LoadCandlesAsync("MSFT", It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ReturnsAsync(compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.D1, 3);

        // Assert
        var alignedMsft = result.Series["MSFT"];
        Assert.NotNull(alignedMsft[1]);
        Assert.Equal(ts2, alignedMsft[1]!.Value.Timestamp);
        Assert.Equal(105m, alignedMsft[1]!.Value.Close); // Carried over from TS1 Close
        Assert.Equal(0, alignedMsft[1]!.Value.Volume);   // Volume should be 0 for filled data
    }

    [Fact]
    public async Task AlignAsync_LeadingGap_ReturnsNullForInitialMisses()
    {
        // Arrange
        var ts1 = new DateTime(2024, 1, 1);
        var ts2 = new DateTime(2024, 1, 2);
        
        var primaryData = new List<CandleData> { new(ts1, 1, 1, 1, 1, 1), new(ts2, 2, 2, 2, 2, 1) };
        var compData = new List<CandleData> { new(ts2, 100, 100, 100, 100, 1) }; // Missing TS1

        _dataServiceMock.Setup(d => d.LoadCandlesAsync(It.IsAny<string>(), It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ReturnsAsync((string s, TimeFrame tf, int c) => s == "AAPL" ? primaryData : compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.D1, 2);

        // Assert
        Assert.Null(result.Series["MSFT"][0]);
        Assert.NotNull(result.Series["MSFT"][1]);
    }

    [Fact]
    public async Task AlignAsync_LongGapExceedsLimit_StopsFilling()
    {
        // Arrange
        var dates = Enumerable.Range(0, 40).Select(i => new DateTime(2024, 1, 1).AddDays(i)).ToList();
        var primaryData = dates.Select(d => new CandleData(d, 1, 1, 1, 1, 1)).ToList();
        
        // MSFT only has the first day
        var compData = new List<CandleData> { new(dates[0], 100, 100, 100, 100, 1) };

        _dataServiceMock.Setup(d => d.LoadCandlesAsync(It.IsAny<string>(), It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ReturnsAsync((string s, TimeFrame tf, int c) => s == "AAPL" ? primaryData : compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.D1, 40);

        // Assert
        Assert.NotNull(result.Series["MSFT"][30]); // Day 30 is still filled (limit=30)
        Assert.Null(result.Series["MSFT"][31]);    // Day 31 is null
    }

    [Fact]
    public async Task AlignAsync_HighNullRatio_ExcludesTicker()
    {
        // Arrange
        var dates = Enumerable.Range(0, 10).Select(i => new DateTime(2024, 1, 1).AddDays(i)).ToList();
        var primaryData = dates.Select(d => new CandleData(d, 1, 1, 1, 1, 1)).ToList();
        
        // MSFT has only 1 day out of 10. (90% null, limit is 50%)
        var compData = new List<CandleData> { new(dates[9], 100, 100, 100, 100, 1) };

        _dataServiceMock.Setup(d => d.LoadCandlesAsync(It.IsAny<string>(), It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ReturnsAsync((string s, TimeFrame tf, int c) => s == "AAPL" ? primaryData : compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.D1, 10);

        // Assert
        Assert.Single(result.Series); // Only PrimarySymbol should remain
        Assert.True(result.Series.ContainsKey("AAPL"));
        Assert.Contains(result.Warnings, w => w.Contains("excluded due to high data fragmentation"));
    }

    [Fact]
    public async Task AlignAsync_ComparisonSymbolFail_SkipsAndWarns()
    {
        // Arrange
        var primaryData = new List<CandleData> { new(new DateTime(2024, 1, 1), 1, 1, 1, 1, 1) };

        _dataServiceMock.Setup(d => d.LoadCandlesAsync("AAPL", It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ReturnsAsync(primaryData);
        _dataServiceMock.Setup(d => d.LoadCandlesAsync("FAIL", It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ThrowsAsync(new Exception("Network error"));

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "FAIL" }, TimeFrame.D1, 1);

        // Assert
        Assert.Single(result.Series); // Only PrimarySymbol should remain
        Assert.True(result.Series.ContainsKey("AAPL"));
        Assert.Contains(result.Warnings, w => w.Contains("Failed to load FAIL"));
    }

    [Fact]
    public async Task AlignAsync_MaxSymbolsLimit_RespectsLimit()
    {
        // Arrange
        var primaryData = new List<CandleData> { new(new DateTime(2024, 1, 1), 1, 1, 1, 1, 1) };
        _dataServiceMock.Setup(d => d.LoadCandlesAsync(It.IsAny<string>(), It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ReturnsAsync(primaryData);

        var symbols = new[] { "S1", "S2", "S3", "S4", "S5", "S6" };

        // Act
        var result = await _aligner.AlignAsync("AAPL", symbols, TimeFrame.D1, 1);

        // Assert
        Assert.Equal(6, result.Series.Count); // 5 Comparisons + 1 Primary
        Assert.DoesNotContain(result.Series.Keys, s => s == "S6");
    }

    [Fact]
    public async Task AlignAsync_DuplicateTimestamps_HandlesGracefully()
    {
        // Arrange
        var ts1 = new DateTime(2024, 1, 1);
        var primaryData = new List<CandleData> { new(ts1, 1, 1, 1, 1, 1) };
        
        // Duplicate timestamp ts1 for MSFT
        var compData = new List<CandleData> { 
            new(ts1, 100, 100, 100, 100, 1),
            new(ts1, 101, 101, 101, 101, 1)
        };

        _dataServiceMock.Setup(d => d.LoadCandlesAsync(It.IsAny<string>(), It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ReturnsAsync((string s, TimeFrame tf, int c) => s == "AAPL" ? primaryData : compData);

        // Act & Assert
        // This should NOT throw ArgumentException even if duplicates exist
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.D1, 1);
        
        Assert.Equal(2, result.Series.Count); // MSFT + AAPL
        Assert.Equal(100m, result.Series["MSFT"][0]!.Value.Close); // Should take the first one or just not crash
    }
}
