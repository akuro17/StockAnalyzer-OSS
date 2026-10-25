using System;
using System.Collections.Generic;
using System.Reflection;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Watchlist;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.ViewModels;

[Collection("MessengerSharedState")]
public class TickerRowParityTests
{
    private static readonly MethodInfo ApplyMetadataMethod = typeof(TickerListViewModel).GetMethod(
        "ApplyMetadataToItem",
        BindingFlags.NonPublic | BindingFlags.Instance,
        null,
        new[] { typeof(WatchlistItemViewModel), typeof(TickerMetadata?) },
        null)!;

    private static readonly MethodInfo ApplyTimeSeriesMethod = typeof(TickerListViewModel).GetMethod(
        "ApplyTimeSeriesDataToItem",
        BindingFlags.NonPublic | BindingFlags.Instance,
        null,
        new[] { typeof(WatchlistItemViewModel), typeof(IReadOnlyList<CandleData>) },
        null)!;

    private static TickerListViewModel CreateListViewModel()
    {
        var mockProvider = new Mock<IMarketDataProvider>();
        mockProvider.Setup(p => p.GetAvailableTickersAsync()).ReturnsAsync(new List<string>());
        return TickerListViewModelTests.CreateViewModel(mockProvider.Object);
    }

    private static void ApplyViaVm(TickerListViewModel vm, WatchlistItemViewModel item, TickerMetadata? meta, IReadOnlyList<CandleData>? candles)
    {
        ApplyMetadataMethod.Invoke(vm, new object?[] { item, meta });
        if (candles != null && candles.Count > 0)
        {
            ApplyTimeSeriesMethod.Invoke(vm, new object?[] { item, candles });
        }
    }

    [Fact]
    public void Parity_FullNonZeroMetadataAndTwoCandles_ProducesIdenticalProperties()
    {
        using var vm = CreateListViewModel();
        var item = new WatchlistItemViewModel("AAPL", "Apple", "Tech", "Hardware", 0, 0, 0, 0, 0, 0, 0);

        var meta = new TickerMetadata(
            ShortName: "Apple Inc.",
            LongName: "Apple Incorporated",
            Region: "US",
            Sector: "Technology",
            Industry: "Consumer Electronics",
            Currency: "USD",
            CurrentPrice: 150m,
            LastClose: 145m)
        {
            Tag = "MegaCap, Hardware",
            Notes = "Core portfolio",
            Reminder = "Review earnings",
            ReturnOnEquity = 25.5m,
            ReturnOnAssets = 15.2m,
            GrossMargins = 42.1m,
            OperatingMargins = 30.5m,
            ProfitMargins = 24.8m,
            CurrentRatio = 1.35m,
            DebtToEquity = 1.8m,
            Ebitda = 120_000_000_000m,
            FreeCashflow = 100_000_000_000m,
            OperatingCashflow = 110_000_000_000m,
            TrailingPE = 28.5m,
            ForwardPE = 25.0m,
            PriceToBook = 40.0m,
            TrailingEps = 6.2m,
            ForwardEps = 7.1m,
            BookValue = 4.2m,
            SharesOutstanding = 15_500_000_000m,
            FloatShares = 15_000_000_000m,
            ShortRatio = 1.5m,
            ShortPercentOfFloat = 2.1m,
            HeldPercentInsiders = 0.5m,
            HeldPercentInstitutions = 60.5m,
            LongBusinessSummary = "Designs consumer electronics",
            FullTimeEmployees = 160_000,
            FiftyTwoWeekHigh = 195.0m,
            FiftyTwoWeekLow = 140.0m,
            RevenueGrowth = 8.5m,
            EarningsGrowth = 10.2m,
            EnterpriseValue = 2_800_000_000_000m,
            EnterpriseToEbitda = 22.5m,
            Beta = 1.25m,
            PayoutRatio = 15.0m,
            DividendRate = 0.95m,
            DividendYield = 0.6m,
            TotalDebt = 110_000_000_000m,
            TotalCash = 60_000_000_000m,
            TotalRevenue = 380_000_000_000m,
            MarketCap = 2_500_000_000_000m,
            PriceToSalesTrailing12Months = 7.2m,
            EnterpriseToRevenue = 7.5m,
            EbitdaMargins = 32.0m,
            QuickRatio = 1.05m,
            AverageVolume = 55_000_000m,
            AverageTurnoverRate = 0.35m,
            AverageFloatTurnover = 0.38m,
            PegRatio = 2.1m,
            QuoteType = "EQUITY",
            ExchangeTimezoneName = "America/New_York",
            GmtOffSetMilliseconds = -14400000,
            ExDividendDate = 1700000000,
            LastFiscalYearEnd = 1695945600,
            MostRecentQuarter = 1703980800,
            TargetHighPrice = 220.0m,
            TargetLowPrice = 160.0m,
            TargetMeanPrice = 190.0m,
            TargetMedianPrice = 188.0m,
            RecommendationKey = "buy",
            RecommendationMean = 1.8m,
            NumberOfAnalystOpinions = 45,
            MetadataLastUpdated = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Long = 145m,
            ExitLong = 190m,
            StopLossLong = 135m,
            Short = 200m,
            ExitShort = 160m,
            StopLossShort = 210m,
            IsLong = true,
            IsTPLong = false,
            IsSLLong = false,
            IsShort = false,
            IsTPShort = false,
            IsSLShort = false
        };

        var candles = new List<CandleData>
        {
            new CandleData(new DateTime(2026, 1, 10), 168m, 172m, 167m, 170m, 50_000_000),
            new CandleData(new DateTime(2026, 1, 11), 171m, 176m, 170m, 175m, 62_000_000)
        };

        ApplyViaVm(vm, item, meta, candles);
        var row = TickerRowCalculator.Compute("AAPL", meta, candles);

        AssertRowsMatch(item, row);
    }

    [Fact]
    public void Parity_CurrentPriceFallbackNoCandles_ProducesIdenticalProperties()
    {
        using var vm = CreateListViewModel();
        var item = new WatchlistItemViewModel("FALLBACK", "Fallback", "Tech", "Hardware", 0, 0, 0, 0, 0, 0, 0);

        var meta = new TickerMetadata(
            ShortName: "Fallback Corp",
            LongName: "Fallback Corporation",
            Region: "US",
            Sector: "Technology",
            Industry: "Software",
            Currency: "USD",
            CurrentPrice: 85.5m,
            LastClose: 82.0m)
        {
            SharesOutstanding = 1_000_000m,
            BookValue = 20m,
            TrailingEps = 4m,
            FiftyTwoWeekHigh = 100m,
            FiftyTwoWeekLow = 60m
        };

        ApplyViaVm(vm, item, meta, null);
        var row = TickerRowCalculator.Compute("FALLBACK", meta, null);

        AssertRowsMatch(item, row);
    }

    [Fact]
    public void Parity_ZeroDivisorEdgeCases_ProducesIdenticalProperties()
    {
        using var vm = CreateListViewModel();
        var item = new WatchlistItemViewModel("ZERO", "Zero Co", "Tech", "Hardware", 0, 0, 0, 0, 0, 0, 0);

        var meta = new TickerMetadata(
            ShortName: "Zero Co",
            LongName: "Zero Company",
            Region: "US",
            Sector: "Technology",
            Industry: "Hardware",
            Currency: "USD")
        {
            SharesOutstanding = 0m,
            BookValue = 0m,
            FullTimeEmployees = 0,
            FiftyTwoWeekHigh = 0m,
            FiftyTwoWeekLow = 50m,
            TrailingEps = 0m,
            ForwardEps = 0m
        };

        var candles = new List<CandleData>
        {
            new CandleData(new DateTime(2026, 1, 15), 10m, 11m, 9m, 10m, 10_000)
        };

        ApplyViaVm(vm, item, meta, candles);
        var row = TickerRowCalculator.Compute("ZERO", meta, candles);

        AssertRowsMatch(item, row);
    }

    [Fact]
    public void Parity_RuleEvaluation_WatchlistFilterEngineAndFilterRuleEvaluatorAgree()
    {
        using var vm = CreateListViewModel();
        var item = new WatchlistItemViewModel("AAPL", "Apple", "Technology", "Consumer Electronics", 150m, 155m, 149m, 152m, 1000000, 2.0, 3.0m)
        {
            Tag = "Core, Tech, MegaCap",
            ReturnOnAssets = 14.5m,
            FiftyTwoWeekHigh = 200m,
            Sector = "Technology"
        };

        var row = new TickerRow
        {
            Symbol = item.Symbol,
            Name = item.Name,
            Sector = item.Sector,
            Industry = item.Industry,
            Open = item.Open,
            High = item.High,
            Low = item.Low,
            Close = item.Close,
            Volume = item.Volume,
            Change = item.Change,
            ChangePercent = item.ChangePercent,
            Tag = item.Tag,
            ReturnOnAssets = item.ReturnOnAssets,
            FiftyTwoWeekHigh = item.FiftyTwoWeekHigh
        };

        var engine = new WatchlistFilterEngine();

        var rulesToTest = new[]
        {
            new FilterRule { Field = "Close", Operator = ">", Value = "150" },
            new FilterRule { Field = "Close", Operator = "<=", Value = "152" },
            new FilterRule { Field = "Close", Operator = "==", Value = "152" },
            new FilterRule { Field = "ROA", Operator = ">", Value = "10" },
            new FilterRule { Field = "ReturnOnAssets", Operator = ">=", Value = "14.5" },
            new FilterRule { Field = "52W High", Operator = "==", Value = "200" },
            new FilterRule { Field = "Tag", Operator = "Contains", Value = "Tech" },
            new FilterRule { Field = "Tag", Operator = "!*=", Value = "Bio" },
            new FilterRule { Field = "Sector", Operator = "==", Value = "Technology" },
            new FilterRule { Field = "Sector", Operator = "Contains", Value = "Tech" },
            new FilterRule { Field = "Close", Operator = "<", Value = "FiftyTwoWeekHigh", IsCompareToField = true },
            new FilterRule { Field = "Close", Operator = ">", Value = "FiftyTwoWeekHigh", IsCompareToField = true },
            new FilterRule { Field = "NonExistent", Operator = ">", Value = "10" }
        };

        foreach (var rule in rulesToTest)
        {
            bool engineResult = engine.EvaluateRule(item, rule);
            bool coreResult = FilterRuleEvaluator.EvaluateRule(row, rule);
            Assert.Equal(engineResult, coreResult);
        }
    }

    private static void AssertRowsMatch(WatchlistItemViewModel item, TickerRow row)
    {
        foreach (var col in WatchlistColumnRegistry.AllColumns)
        {
            if (string.Equals(col.MemberName, "IsChecked", StringComparison.OrdinalIgnoreCase))
                continue;

            var itemProp = typeof(WatchlistItemViewModel).GetProperty(col.MemberName);
            var rowProp = typeof(TickerRow).GetProperty(col.MemberName);

            Assert.NotNull(itemProp);
            Assert.NotNull(rowProp);

            var itemVal = itemProp.GetValue(item);
            var rowVal = rowProp.GetValue(row);

            if (itemVal is decimal itemDec && rowVal is decimal rowDec)
            {
                Assert.True(itemDec == rowDec, $"Mismatch on {col.MemberName}: VM={itemDec}, Row={rowDec}");
            }
            else if (itemVal is double itemDbl && rowVal is double rowDbl)
            {
                Assert.True(Math.Abs(itemDbl - rowDbl) < 0.0001, $"Mismatch on {col.MemberName}: VM={itemDbl}, Row={rowDbl}");
            }
            else if (itemVal is DateTimeOffset itemDto && rowVal is DateTimeOffset rowDto)
            {
                Assert.True(itemDto == rowDto, $"Mismatch on {col.MemberName}: VM={itemDto}, Row={rowDto}");
            }
            else if (itemVal is DateTime itemDt && rowVal is DateTime rowDt)
            {
                Assert.True(itemDt == rowDt, $"Mismatch on {col.MemberName}: VM={itemDt}, Row={rowDt}");
            }
            else
            {
                Assert.True(Equals(itemVal, rowVal), $"Mismatch on {col.MemberName}: VM={itemVal}, Row={rowVal}");
            }
        }
    }
}
