using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// The start price of a performance period (MonthToDate, YearToDate, OneYear) is decided from the stored data, not from the day the code runs:
/// the reference date is the newest bar date over all held symbols. The result must not depend on <see cref="DateTime.Now"/>.
/// </summary>
public sealed class PortfolioPerformanceReferenceDateTests
{
    private const decimal EndPrice = 1000m;

    private static List<CandleData> BusinessDayCandles(DateTime first, DateTime last)
    {
        var candles = new List<CandleData>();
        var index = 0;
        for (var day = first.Date; day <= last.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday) continue;
            var close = 100m + index++;
            candles.Add(new CandleData(day, close, close + 1m, close - 1m, close, 1000));
        }
        return candles;
    }

    private static decimal CloseOn(IReadOnlyList<CandleData> candles, DateTime date) => candles.Single(c => c.Timestamp.Date == date.Date).Close;

    private static async Task<IReadOnlyList<HeatmapEntry>> RunAsync(IReadOnlyDictionary<string, List<CandleData>> series, PerformancePeriod period)
    {
        var positions = series.Keys.ToDictionary(symbol => symbol, symbol => new Position(symbol, 10, 100));
        var portfolio = new Portfolio(0, positions.ToImmutableDictionary(), ImmutableList<Transaction>.Empty);

        var provider = new Mock<IMarketDataProvider>();
        provider.Setup(p => p.GetLatestPricesAsync(It.IsAny<IEnumerable<string>>()))
            .ReturnsAsync((IEnumerable<string> symbols) => (IReadOnlyDictionary<string, decimal>)symbols.ToDictionary(s => s, _ => EndPrice));
        provider.Setup(p => p.GetTickersDataAsync(It.IsAny<string>(), It.IsAny<TimeFrame>()))
            .ReturnsAsync((string symbol, TimeFrame _) => (IReadOnlyList<CandleData>)series[symbol]);
        provider.Setup(p => p.GetMetadataAsync(It.IsAny<string>()))
            .ReturnsAsync((string t) => new TickerMetadata(t, t, "US", "Tech", "Software", "USD"));

        return await new PortfolioManager().GetPerformanceHeatmapAsync(portfolio, period, provider.Object);
    }

    private static float ExpectedReturn(decimal startPrice) => (float)((EndPrice - startPrice) / startPrice);

    [Fact]
    public async Task MonthToDate_UsesTheLastCloseBeforeTheMonthOfTheNewestStoredBar()
    {
        var candles = BusinessDayCandles(new DateTime(2023, 1, 2), new DateTime(2024, 3, 15));

        var entries = await RunAsync(new Dictionary<string, List<CandleData>> { ["AAPL"] = candles }, PerformancePeriod.MonthToDate);

        Assert.Equal(ExpectedReturn(CloseOn(candles, new DateTime(2024, 2, 29))), Assert.Single(entries).Return);
    }

    [Fact]
    public async Task YearToDate_UsesTheLastCloseBeforeTheYearOfTheNewestStoredBar()
    {
        var candles = BusinessDayCandles(new DateTime(2023, 1, 2), new DateTime(2024, 3, 15));

        var entries = await RunAsync(new Dictionary<string, List<CandleData>> { ["AAPL"] = candles }, PerformancePeriod.YearToDate);

        Assert.Equal(ExpectedReturn(CloseOn(candles, new DateTime(2023, 12, 29))), Assert.Single(entries).Return);
    }

    [Fact]
    public async Task OneYear_UsesTheNewestCloseBeforeTheSameDayOneYearBeforeTheNewestStoredBar()
    {
        var candles = BusinessDayCandles(new DateTime(2023, 1, 2), new DateTime(2024, 3, 15));

        var entries = await RunAsync(new Dictionary<string, List<CandleData>> { ["AAPL"] = candles }, PerformancePeriod.OneYear);

        // Boundary 2023-03-15; the start bar is the newest bar strictly before it (the rule of FindNearestIndex(...) - 1, unchanged).
        Assert.Equal(ExpectedReturn(CloseOn(candles, new DateTime(2023, 3, 14))), Assert.Single(entries).Return);
    }

    [Fact]
    public async Task MonthToDate_ALaggingSymbolUsesTheCommonReferenceDateAndItsOwnLatestCloseBeforeTheBoundary()
    {
        var fresh = BusinessDayCandles(new DateTime(2023, 1, 2), new DateTime(2024, 3, 15));
        var lagging = BusinessDayCandles(new DateTime(2023, 1, 2), new DateTime(2024, 2, 28));

        var entries = await RunAsync(
            new Dictionary<string, List<CandleData>> { ["FRESH"] = fresh, ["LAGGING"] = lagging },
            PerformancePeriod.MonthToDate);

        // Common reference = 2024-03-15, boundary 2024-03-01: the lagging symbol has no bar on or after it, so its start is its own last close (2024-02-28).
        // A per-symbol reference (2024-02-28) would have given the boundary 2024-02-01 and the 2024-01-31 close instead.
        Assert.Equal(ExpectedReturn(CloseOn(fresh, new DateTime(2024, 2, 29))), entries.Single(e => e.Ticker == "FRESH").Return);
        Assert.Equal(ExpectedReturn(CloseOn(lagging, new DateTime(2024, 2, 28))), entries.Single(e => e.Ticker == "LAGGING").Return);
    }

    [Fact]
    public async Task MonthToDate_ASymbolWhoseProviderAnswersNull_IsNotANumber_AndDoesNotBreakTheOthers()
    {
        var fresh = BusinessDayCandles(new DateTime(2023, 1, 2), new DateTime(2024, 3, 15));

        var entries = await RunAsync(
            new Dictionary<string, List<CandleData>> { ["FRESH"] = fresh, ["NULLDATA"] = null! },
            PerformancePeriod.MonthToDate);

        Assert.Equal(ExpectedReturn(CloseOn(fresh, new DateTime(2024, 2, 29))), entries.Single(e => e.Ticker == "FRESH").Return);
        Assert.True(float.IsNaN(entries.Single(e => e.Ticker == "NULLDATA").Return));
    }

    [Fact]
    public async Task MonthToDate_WithoutAnyStoredBar_StaysNotANumber()
    {
        var entries = await RunAsync(new Dictionary<string, List<CandleData>> { ["AAPL"] = new List<CandleData>() }, PerformancePeriod.MonthToDate);

        Assert.True(float.IsNaN(Assert.Single(entries).Return));
    }
}
