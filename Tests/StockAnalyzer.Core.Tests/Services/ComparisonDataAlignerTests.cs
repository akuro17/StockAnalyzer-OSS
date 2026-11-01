using Microsoft.Extensions.Logging;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Watchlist;
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
        _aligner.MaxComparisonSymbols = 5;

        // Act
        var result = await _aligner.AlignAsync("AAPL", symbols, TimeFrame.D1, 1);

        // Assert
        Assert.Equal(6, result.Series.Count); // 5 Comparisons + 1 Primary
        Assert.DoesNotContain(result.Series.Keys, s => s == "S6");
    }

    [Fact]
    public async Task AlignAsync_StandardLimit_IsWhatTheSettingsDialogAllows()
    {
        // Arrange
        var primaryData = new List<CandleData> { new(new DateTime(2024, 1, 1), 1, 1, 1, 1, 1) };
        _dataServiceMock.Setup(d => d.LoadCandlesAsync(It.IsAny<string>(), It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ReturnsAsync(primaryData);

        int dialogLimit = WatchlistConstants.MaxRelativePerformanceTargets;
        var symbols = Enumerable.Range(1, dialogLimit + 1).Select(i => $"S{i}").ToArray();

        // Act
        var result = await _aligner.AlignAsync("AAPL", symbols, TimeFrame.D1, 1);

        // Assert
        Assert.Equal(dialogLimit, _aligner.MaxComparisonSymbols);
        Assert.Equal(dialogLimit + 1, result.Series.Count); // every registrable comparison symbol + the primary
        Assert.DoesNotContain(result.Series.Keys, s => s == $"S{dialogLimit + 1}");
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

    // W1 and MN1 bars built by TimeFrameAggregator are dated by the first TRADING day of the period, so two markets with different holidays date the
    // same week or month differently. For these frames the comparison bar of the same ISO week / calendar month is the match, not the same date.

    [Fact]
    public async Task AlignAsync_Weekly_HolidayShiftedLabel_MatchesSamePeriodAndRestampsToPrimaryLabel()
    {
        // Arrange: 2024-01-15 is a US holiday (primary starts on Tuesday 01-16) and a Japanese trading day (comparison starts on Monday 01-15).
        var primaryData = new List<CandleData> { new(new DateTime(2024, 1, 9), 10, 11, 9, 10, 100), new(new DateTime(2024, 1, 16), 20, 21, 19, 20, 200) };
        var compData = new List<CandleData> { new(new DateTime(2024, 1, 8), 100, 110, 90, 105, 1000), new(new DateTime(2024, 1, 15), 200, 210, 190, 205, 2000) };
        SetupFrameData(primaryData, compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.W1, 2);

        // Assert
        CandleData?[] aligned = result.Series["MSFT"];
        Assert.Equal(new CandleData(new DateTime(2024, 1, 9), 100, 110, 90, 105, 1000), aligned[0]);
        Assert.Equal(new CandleData(new DateTime(2024, 1, 16), 200, 210, 190, 205, 2000), aligned[1]);
        Assert.Equal(new[] { new DateTime(2024, 1, 9), new DateTime(2024, 1, 16) }, result.Timestamps);
    }

    [Fact]
    public async Task AlignAsync_Monthly_HolidayShiftedLabel_MatchesSamePeriodAndRestampsToPrimaryLabel()
    {
        // Arrange: the first trading day of January 2024 is 01-02 in the US and 01-04 in Japan.
        var primaryData = new List<CandleData> { new(new DateTime(2023, 12, 1), 10, 11, 9, 10, 100), new(new DateTime(2024, 1, 2), 20, 21, 19, 20, 200) };
        var compData = new List<CandleData> { new(new DateTime(2023, 12, 1), 100, 110, 90, 105, 1000), new(new DateTime(2024, 1, 4), 200, 210, 190, 205, 2000) };
        SetupFrameData(primaryData, compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.MN1, 2);

        // Assert
        CandleData?[] aligned = result.Series["MSFT"];
        Assert.Equal(new CandleData(new DateTime(2023, 12, 1), 100, 110, 90, 105, 1000), aligned[0]);
        Assert.Equal(new CandleData(new DateTime(2024, 1, 2), 200, 210, 190, 205, 2000), aligned[1]);
    }

    [Fact]
    public async Task AlignAsync_Weekly_IsoYearBoundary_KeepsWeeksApart()
    {
        // Arrange: 2021-01-01 (Friday) still belongs to ISO week 2020-W53 (key 202053), 2021-01-05 to 2021-W01 (key 202101).
        var primaryData = new List<CandleData> { new(new DateTime(2020, 12, 28), 10, 11, 9, 10, 100), new(new DateTime(2021, 1, 4), 20, 21, 19, 20, 200) };
        var compData = new List<CandleData> { new(new DateTime(2021, 1, 1), 100, 110, 90, 105, 1000), new(new DateTime(2021, 1, 5), 200, 210, 190, 205, 2000) };
        SetupFrameData(primaryData, compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.W1, 2);

        // Assert
        CandleData?[] aligned = result.Series["MSFT"];
        Assert.Equal(new CandleData(new DateTime(2020, 12, 28), 100, 110, 90, 105, 1000), aligned[0]);
        Assert.Equal(new CandleData(new DateTime(2021, 1, 4), 200, 210, 190, 205, 2000), aligned[1]);
    }

    [Theory]
    [InlineData(TimeFrame.W1)]
    [InlineData(TimeFrame.MN1)]
    public async Task AlignAsync_WeeklyAndMonthly_IdenticalLabels_AreUnchanged(TimeFrame frame)
    {
        // Arrange
        var primaryData = new List<CandleData> { new(new DateTime(2024, 1, 2), 10, 11, 9, 10, 100), new(new DateTime(2024, 2, 1), 20, 21, 19, 20, 200) };
        var compData = new List<CandleData> { new(new DateTime(2024, 1, 2), 100, 110, 90, 105, 1000), new(new DateTime(2024, 2, 1), 200, 210, 190, 205, 2000) };
        SetupFrameData(primaryData, compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, frame, 2);

        // Assert
        Assert.Equal(compData.Select(c => (CandleData?)c), result.Series["MSFT"]);
    }

    [Fact]
    public async Task AlignAsync_Weekly_NoComparisonBarInPeriod_CarriesCloseForwardOnPrimaryLabel()
    {
        // Arrange: the comparison symbol has no bar for the second week.
        var primaryData = new List<CandleData>
        {
            new(new DateTime(2024, 1, 2), 1, 1, 1, 1, 1), new(new DateTime(2024, 1, 9), 1, 1, 1, 1, 1), new(new DateTime(2024, 1, 16), 1, 1, 1, 1, 1)
        };
        var compData = new List<CandleData> { new(new DateTime(2024, 1, 1), 100, 110, 90, 105, 1000), new(new DateTime(2024, 1, 15), 200, 210, 190, 205, 2000) };
        SetupFrameData(primaryData, compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.W1, 3);

        // Assert
        CandleData?[] aligned = result.Series["MSFT"];
        Assert.Equal(new CandleData(new DateTime(2024, 1, 2), 100, 110, 90, 105, 1000), aligned[0]);
        Assert.Equal(new CandleData(new DateTime(2024, 1, 9), 105, 105, 105, 105, 0), aligned[1]);
        Assert.Equal(new CandleData(new DateTime(2024, 1, 16), 200, 210, 190, 205, 2000), aligned[2]);
    }

    [Fact]
    public async Task AlignAsync_Daily_SamePeriodButDifferentDate_StaysUnmatched()
    {
        // Arrange: the same ISO week, but the daily frame still matches by exact date only.
        var primaryData = new List<CandleData> { new(new DateTime(2024, 1, 16), 1, 1, 1, 1, 1) };
        var compData = new List<CandleData> { new(new DateTime(2024, 1, 15), 100, 100, 100, 100, 1) };
        SetupFrameData(primaryData, compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.D1, 1);

        // Assert: no comparison bar on the date and nothing before it to carry, so the symbol is excluded as fragmented.
        Assert.False(result.Series.ContainsKey("MSFT"));
        Assert.Contains(result.Warnings, w => w.Contains("excluded due to high data fragmentation"));
    }

    [Fact]
    public async Task AlignAsync_Weekly_TwoComparisonBarsOfOnePeriod_FirstWins()
    {
        // Arrange
        var primaryData = new List<CandleData> { new(new DateTime(2024, 1, 16), 1, 1, 1, 1, 1) };
        var compData = new List<CandleData> { new(new DateTime(2024, 1, 15), 100, 100, 100, 100, 1), new(new DateTime(2024, 1, 17), 101, 101, 101, 101, 1) };
        SetupFrameData(primaryData, compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.W1, 1);

        // Assert
        Assert.Equal(100m, result.Series["MSFT"][0]!.Value.Close);
    }

    [Theory]
    [InlineData(TimeFrame.D1)]
    [InlineData(TimeFrame.W1)]
    public async Task AlignAsync_FragmentationGate_IsExcludedAboveHalfAndKeptAtExactlyHalf(TimeFrame frame)
    {
        // Arrange: 10 primary bars, one per day (D1) or per week (W1); the comparison symbol starts late, so the leading bars stay empty.
        int step = frame == TimeFrame.W1 ? 7 : 1;
        var primaryDates = Enumerable.Range(0, 10).Select(i => new DateTime(2024, 1, 1).AddDays(i * step)).ToList();
        var primaryData = primaryDates.Select(d => new CandleData(d, 1, 1, 1, 1, 1)).ToList();
        int shift = frame == TimeFrame.W1 ? 1 : 0; // W1: the comparison label is one day later in the same week
        var exactlyHalf = primaryDates.Skip(5).Select(d => new CandleData(d.AddDays(shift), 100, 100, 100, 100, 1)).ToList();
        var aboveHalf = primaryDates.Skip(6).Select(d => new CandleData(d.AddDays(shift), 100, 100, 100, 100, 1)).ToList();

        // Act
        SetupFrameData(primaryData, exactlyHalf);
        var kept = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, frame, 10);
        SetupFrameData(primaryData, aboveHalf);
        var excluded = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, frame, 10);

        // Assert: 5 of 10 empty is not above the 50 % limit, 6 of 10 is.
        Assert.True(kept.Series.ContainsKey("MSFT"));
        Assert.Equal(5, kept.Series["MSFT"].Count(c => c == null));
        Assert.False(excluded.Series.ContainsKey("MSFT"));
        Assert.Contains(excluded.Warnings, w => w.Contains("MSFT excluded due to high data fragmentation (60.0% missing)."));
    }

    [Fact]
    public async Task AlignAsync_Weekly_HolidayShiftedLabel_GivesCrossTickerIndicatorsAValueForEveryMatchedWeek()
    {
        // Arrange: three weeks; Japanese labels are one day earlier than the US ones (US Monday holiday every week, for the test).
        var weeks = new[] { new DateTime(2024, 1, 2), new DateTime(2024, 1, 9), new DateTime(2024, 1, 16) };
        var primaryData = weeks.Select((d, i) => new CandleData(d, 10 + i, 10 + i, 10 + i, 10 + i, 1)).ToList();
        var compData = weeks.Select((d, i) => new CandleData(d.AddDays(-1), 50 + i, 50 + i, 50 + i, 50 + i, 1)).ToList();
        SetupFrameData(primaryData, compData);

        // Act
        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.W1, 3);
        var primary = primaryData.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
        var secondary = result.Series["MSFT"].Select(c => c is { } bar ? new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume) : null).ToList();
        List<decimal?> series = PriceDataHelper.ExtractDateAlignedPriceSeries(primary, secondary, PriceType.Close);

        // Assert: the helper every cross-ticker indicator calls finds all three comparison closes.
        Assert.Equal(new decimal?[] { 50m, 51m, 52m }, series);
    }

    [Fact]
    public async Task AlignAsync_Weekly_HolidayShiftedLabel_CorrelationIndicatorGetsAValueFromTheAlignedSeries()
    {
        // Arrange: six weeks; the primary symbol is dated on the Tuesday and the comparison symbol on the Monday of each week.
        var mondays = Enumerable.Range(0, 6).Select(i => new DateTime(2024, 1, 1).AddDays(7 * i)).ToList();
        var primaryData = mondays.Select((d, i) => new CandleData(d.AddDays(1), 10 + i, 10 + i, 10 + i, 10 + i, 1)).ToList();
        var compData = mondays.Select((d, i) => new CandleData(d, 20 + 2 * i, 20 + 2 * i, 20 + 2 * i, 20 + 2 * i, 1)).ToList();
        SetupFrameData(primaryData, compData);

        var result = await _aligner.AlignAsync("AAPL", new[] { "MSFT" }, TimeFrame.W1, 6);
        var primary = primaryData.Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
        var secondary = result.Series["MSFT"].Select(c => c is { } bar ? new CoreCandleData(bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume) : null).ToList();

        var indicator = new CoreCorrelationIndicator();
        indicator.Configure(new CoreCorrelationParameter { Period = 5, ComparisonSymbol = "MSFT", ComparisonPriceSource = PriceType.Close });
        indicator.SetSecondaryCandles(secondary);

        // Act
        var calculation = indicator.Calculate(primary);

        // Assert: the two series rise in lockstep, so the last value is exactly 1; with the exact-date match there would be no value at all.
        Assert.True(calculation.IsSuccessful);
        Assert.NotNull(indicator.Values.Last());
        Assert.Equal(1.0m, indicator.Values.Last()!.Value, 4);
    }

    private void SetupFrameData(List<CandleData> primaryData, List<CandleData> compData)
        => _dataServiceMock.Setup(d => d.LoadCandlesAsync(It.IsAny<string>(), It.IsAny<TimeFrame>(), It.IsAny<int>()))
            .ReturnsAsync((string s, TimeFrame tf, int c) => s == "AAPL" ? primaryData : compData);
}
