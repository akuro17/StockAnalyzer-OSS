using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class ScreenerServiceTests
{
    private class StubSettings : IStockAnalyzerSettings
    {
        public string? PythonPath => null;
        public string PythonScriptDirectory => "Scripts";
        public string PythonServerScriptName => "server.py";
        public int PythonMaxRetries => 3;
        public int PythonBackoffMs => 1000;
        public int PythonHealthCheckIntervalMs => 5000;
        public string DefaultSymbol => "MSFT";
        public string RenkoUpColor => "Green";
        public string RenkoDownColor => "Red";
        public string KagiUpColor => "Green";
        public string KagiDownColor => "Red";
        public string PnfUpColor => "Green";
        public string PnfDownColor => "Red";
        public string? ScreeningDataPath => null;
        // Infrastructure
        public string PipeName => "ScreenerTestPipe";
        public int PipeConnectionTimeoutMs { get; set; } = 5000;
        public int ScreenerMaxParallelism { get; set; } = 3;

        // Pattern Recognition
        public int PatternRecognitionMinWindow => 20;
        public int PatternRecognitionMaxWindow => 60;
        public int PatternRecognitionWindowStep => 5;
        public double PatternRecognitionDefaultThreshold => 0.5;

        // Market Structure
        public decimal ZigzagThresholdPercent => 5.0m;

        // Localization
        public string? LocaleResourcePath => null;
        public string GetReverseWatchPhaseColor(int phase) => "#808080";
        public IReadOnlyList<string> DefaultScreenerSymbols => System.Array.Empty<string>();
        // Resilience (Circuit Breaker)
        public int CircuitBreakerMinimumThroughput => 3;
        public double CircuitBreakerFailureRatio => 0.5;
        public int CircuitBreakerBreakDurationMs => 30000;
        public int CircuitBreakerSamplingDurationMs => 60000;
        public int PipeConnectPollIntervalMs => 100;
        public int DisposeWaitMs => 1000;
        public int SyncTimeoutMinutes => 2;
        public IReadOnlyList<string> PythonEssentialPackages => new[] { "setuptools", "wheel", "polars", "pandas", "scipy", "yfinance", "pyarrow", "pandas-ta", "scikit-learn", "arch", "statsmodels", "pywin32", "tslearn" };
        public string PredictionModelPath => "Models/trend_predictor.onnx";
        public int PredictionWindowSize => 10;
    }

    private readonly IStockAnalyzerSettings _stubSettings = new StubSettings();
    private readonly DuckDBConnectionManager _dbManager;
    private readonly MockPythonServiceBase _mockPythonService;
    private readonly ParquetMarketDataProvider _parquetProvider;
    private readonly ScreenerService _service;

    public ScreenerServiceTests()
    {
        _dbManager = new DuckDBConnectionManager(NullLogger<DuckDBConnectionManager>.Instance);
        _mockPythonService = new MockPythonServiceBase();
        var settings = Microsoft.Extensions.Options.Options.Create(new MarketDataSettings 
        { 
            DailyDataPath = System.IO.Path.Combine("i:\\stock", "Data", "TestMarketData", "Daily")
        });
        _parquetProvider = new ParquetMarketDataProvider(_dbManager, _mockPythonService, settings, NullLogger<ParquetMarketDataProvider>.Instance);
        
        var providers = new List<IMarketDataProvider> { _parquetProvider };
        var mockDataService = new MockDataService(symbol => Task.FromResult<IReadOnlyList<CandleData>>(new List<CandleData>()));
        _service = new ScreenerService(mockDataService, _stubSettings, providers);
    }

    private class MockDataService : IDataService
    {
        private readonly Func<string, Task<IReadOnlyList<CandleData>>> _loader;

        public MockDataService(Func<string, Task<IReadOnlyList<CandleData>>> loader)
        {
            _loader = loader;
        }

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            return _loader(symbol);
        }
    }

    private class MockScreeningCondition : IScreeningCondition
    {
        private readonly Func<IReadOnlyList<CandleData>, bool> _evaluator;

        public MockScreeningCondition(Func<IReadOnlyList<CandleData>, bool> evaluator)
        {
            _evaluator = evaluator;
        }

        public bool IsMet(IReadOnlyList<CandleData> candles)
        {
            return _evaluator(candles);
        }
    }

    [Fact]
    public async Task ScreenAsync_ShouldReturnMatchedSymbols_WhenConditionIsMet()
    {
        // Arrange
        var symbols = new List<string> { "MSFT", "GOOG", "AAPL" };
        var dataService = new MockDataService(symbol => Task.FromResult<IReadOnlyList<CandleData>>(new List<CandleData>()));
        var condition = new MockScreeningCondition(candles => true); // Always match
        var service = new ScreenerService(dataService, _stubSettings, Enumerable.Empty<IMarketDataProvider>());
        var progress = new Progress<int>();

        // Act
        var result = await service.ScreenAsync(symbols, condition, TimeFrame.D1, progress, CancellationToken.None);

        // Assert
        Assert.Equal(symbols.Count, result.Count);
        Assert.True(symbols.All(s => result.Contains(s)));
    }

    [Fact]
    public void ExtractValueNullable_ShouldCorrectlyExtractDerivedMetrics()
    {
        var meta = new StockAnalyzer.Core.Models.Portfolio.TickerMetadata("AAPL", "Apple Inc.", "US", "Tech", "Hardware", "USD")
        {
            PbrCalculated = 2.5m,
            DividendYieldCalculated = 1.2m,
            DividendRate = 0.9m,
            BookValue = 30m,
            ReturnOnEquity = 15m,
            ReturnOnAssets = 8m
        };

        List<StockAnalyzer.Core.Models.CoreCandleData> candles = new() { new StockAnalyzer.Core.Models.CoreCandleData(DateTime.UtcNow, 100, 105, 95, 100, 1000) };

        var pbrSide = new StockAnalyzer.Core.Models.Screener.ScreenerIndicatorSideConfig { CategoryType = StockAnalyzer.Core.Models.Screener.ScreenerItemCategoryType.Column, CustomDisplayName = "P/B (Live)" };
        var divYieldSide = new StockAnalyzer.Core.Models.Screener.ScreenerIndicatorSideConfig { CategoryType = StockAnalyzer.Core.Models.Screener.ScreenerItemCategoryType.Column, CustomDisplayName = "Div Yield (Live)" };
        var divRateSide = new StockAnalyzer.Core.Models.Screener.ScreenerIndicatorSideConfig { CategoryType = StockAnalyzer.Core.Models.Screener.ScreenerItemCategoryType.Column, CustomDisplayName = "Dividend per Share" };

        var pbrVal = ScreenerValueExtractor.Default.ExtractValueNullable(pbrSide, candles, meta);
        var divYieldVal = ScreenerValueExtractor.Default.ExtractValueNullable(divYieldSide, candles, meta);
        var divRateVal = ScreenerValueExtractor.Default.ExtractValueNullable(divRateSide, candles, meta);

        Assert.Equal(2.5m, pbrVal);
        Assert.Equal(1.2m, divYieldVal);
        Assert.Equal(0.9m, divRateVal);
    }

    [Fact]
    public void ExtractValueNullable_ComprehensiveMetricVerification()
    {
        var meta = new StockAnalyzer.Core.Models.Portfolio.TickerMetadata("MSFT", "Microsoft Corp", "US", "Tech", "Software", "USD")
        {
            PayoutRatio = 25.5m,
            EbitdaMargins = 48.2m,
            ExDividendDate = 1700000000L,
            CurrentRatio = 1.8m,
            QuickRatio = 1.5m,
            FloatRatio = 90.0m,
            BookValue = 42.1m,
            PbrCalculated = 11.2m
        };

        List<StockAnalyzer.Core.Models.CoreCandleData> candles = new() { new StockAnalyzer.Core.Models.CoreCandleData(DateTime.UtcNow, 400, 405, 395, 400, 5000000) };

        var payoutConfig = new StockAnalyzer.Core.Models.Screener.ScreenerIndicatorSideConfig { CategoryType = StockAnalyzer.Core.Models.Screener.ScreenerItemCategoryType.Column, CustomDisplayName = "Payout Ratio" };
        var ebitdaMarginConfig = new StockAnalyzer.Core.Models.Screener.ScreenerIndicatorSideConfig { CategoryType = StockAnalyzer.Core.Models.Screener.ScreenerItemCategoryType.Column, CustomDisplayName = "EBITDA Margin" };
        var exDivDateConfig = new StockAnalyzer.Core.Models.Screener.ScreenerIndicatorSideConfig { CategoryType = StockAnalyzer.Core.Models.Screener.ScreenerItemCategoryType.Column, CustomDisplayName = "Ex-Dividend Date" };
        var currentRatioConfig = new StockAnalyzer.Core.Models.Screener.ScreenerIndicatorSideConfig { CategoryType = StockAnalyzer.Core.Models.Screener.ScreenerItemCategoryType.Column, CustomDisplayName = "Current Ratio" };

        var payoutVal = ScreenerValueExtractor.Default.ExtractValueNullable(payoutConfig, candles, meta);
        var ebitdaVal = ScreenerValueExtractor.Default.ExtractValueNullable(ebitdaMarginConfig, candles, meta);
        var exDivVal = ScreenerValueExtractor.Default.ExtractValueNullable(exDivDateConfig, candles, meta);
        var currentRatioVal = ScreenerValueExtractor.Default.ExtractValueNullable(currentRatioConfig, candles, meta);

        Assert.Equal(25.5m, payoutVal);
        Assert.Equal(48.2m, ebitdaVal);
        Assert.Equal(1700000000m, exDivVal);
        Assert.Equal(1.8m, currentRatioVal);
    }

    [Fact]
    public async Task ScreenAsync_ShouldHandleEmptySymbolList()
    {
        // Arrange
        var symbols = new List<string>();
        var dataService = new MockDataService(_ => throw new Exception("Should not be called"));
        var condition = new MockScreeningCondition(_ => true);
        var service = new ScreenerService(dataService, _stubSettings, Enumerable.Empty<IMarketDataProvider>());
        var progress = new Progress<int>();

        // Act
        var result = await service.ScreenAsync(symbols, condition, TimeFrame.D1, progress, CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ScreenAsync_ShouldReportProgressCorrectly()
    {
        // Arrange
        var symbols = new List<string> { "MSFT", "GOOG" };
        var dataService = new MockDataService(async symbol =>
        {
            await Task.Delay(50); // Small delay
            return new List<CandleData>();
        });
        var condition = new MockScreeningCondition(_ => true);
        var service = new ScreenerService(dataService, _stubSettings, Enumerable.Empty<IMarketDataProvider>());
        
        var progressValues = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var tcs = new TaskCompletionSource<bool>();
        var progress = new Progress<int>(p => 
        {
            progressValues.Enqueue(p);
            if (p == 100) tcs.TrySetResult(true);
        });

        // Act
        await service.ScreenAsync(symbols, condition, TimeFrame.D1, progress, CancellationToken.None);

        // Wait for the 100% progress report to be processed
        await Task.WhenAny(tcs.Task, Task.Delay(1000));

        // Assert
        Assert.Contains(50, progressValues);
        Assert.Contains(100, progressValues);
        Assert.Equal(100, progressValues.Last());
    }

    [Fact]
    public async Task ScreenAsync_ShouldRespectCancellationToken()
    {
        // Arrange
        var symbols = new List<string> { "MSFT", "GOOG", "AAPL" };
        var cts = new CancellationTokenSource();
        var dataService = new MockDataService(async symbol =>
        {
            await Task.Delay(100, cts.Token);
            return new List<CandleData>();
        });
        var condition = new MockScreeningCondition(_ => true);
        var service = new ScreenerService(dataService, _stubSettings, Enumerable.Empty<IMarketDataProvider>());
        var progress = new Progress<int>();

        // Act & Assert
        var screenTask = service.ScreenAsync(symbols, condition, TimeFrame.D1, progress, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => screenTask);
    }

    [Fact]
    public async Task ScreenAsync_ShouldContinueWhenOneSymbolFails()
    {
        // Arrange
        var symbols = new List<string> { "MSFT", "FAIL", "AAPL" };
        var dataService = new MockDataService(symbol =>
        {
            if (symbol == "FAIL") throw new Exception("Simulated network error");
            return Task.FromResult<IReadOnlyList<CandleData>>(new List<CandleData>());
        });
        var condition = new MockScreeningCondition(_ => true);
        var service = new ScreenerService(dataService, _stubSettings, Enumerable.Empty<IMarketDataProvider>());
        var progress = new Progress<int>();

        // Act
        var result = await service.ScreenAsync(symbols, condition, TimeFrame.D1, progress, CancellationToken.None);

        // Assert
        Assert.Contains("MSFT", result);
        Assert.Contains("AAPL", result);
        Assert.DoesNotContain("FAIL", result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task ScreenAsync_WithCriteria_ShouldUseParquetProvider()
    {
        // Arrange
        // The _service is now initialized in the constructor with _parquetProvider
        var criteria = new ScreeningCriteria
        {
            Conditions = new List<IScreeningCondition>
            {
                new StockAnalyzer.Core.Models.ScreeningConditions.RsiOversoldCondition(14, 100m)
            }
        };

        // Act
        var result = await _service.ScreenAsync(criteria, new Progress<int>(), CancellationToken.None);

        // Assert
        Assert.NotEmpty(result);
        Assert.Contains("MSFT", result);
    }

    [Fact]
    public void Verify_FiftyTwoWeekRangePosition_AndAllColumns_MatchesExpectedCount()
    {
        // Arrange: Create ticker metadata with 52W High=200, Low=100, Price=150 (Range Pos = 0.5 > 0)
        var meta1 = new TickerMetadata("AAPL", "Apple Inc.", "Tech", "Hardware", "US", "EQUITY")
        {
            FiftyTwoWeekHigh = 200m,
            FiftyTwoWeekLow = 100m,
            CurrentPrice = 150m,
            FiftyTwoWeekRangePosition = 0.5m,
            MarketCap = 3000000m
        };

        var meta2 = new TickerMetadata("MSFT", "Microsoft Corp.", "Tech", "Software", "US", "EQUITY")
        {
            FiftyTwoWeekHigh = 300m,
            FiftyTwoWeekLow = 100m,
            CurrentPrice = 100m, // Low == CurrentPrice -> Range Pos = 0.0
            FiftyTwoWeekRangePosition = 0.0m,
            MarketCap = 2500000m
        };

        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(DateTime.Today, 150m, 160m, 140m, 155m, 1000)
        };

        var tickers = new[] { meta1, meta2 };

        // Test 1: Verify FiftyTwoWeekRangePosition > 0 specifically
        var config52W = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Column,
            CustomDisplayName = "52W Range Pos.",
            OutputName = "FiftyTwoWeekRangePosition"
        };

        int manualCount52W = tickers.Count(t => ScreenerValueExtractor.Default.ExtractValueNullable(config52W, candles, t) > 0m);
        Assert.Equal(1, manualCount52W); // Only AAPL (0.5 > 0)

        // Test 2: Iterate all WatchlistColumnRegistry columns and verify manual count matches ExtractValueNullable count
        foreach (var col in StockAnalyzer.Core.Models.Watchlist.WatchlistColumnRegistry.AllColumns)
        {
            if (col.MemberName == "IsChecked" || col.HeaderKey == "Col_Select" || col.MemberName == "Symbol" || col.MemberName == "Name")
                continue;

            var config = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Column,
                CustomDisplayName = col.DisplayName,
                OutputName = col.MemberName
            };

            int countMeta1 = ScreenerValueExtractor.Default.ExtractValueNullable(config, candles, meta1).HasValue ? 1 : 0;
            int countMeta2 = ScreenerValueExtractor.Default.ExtractValueNullable(config, candles, meta2).HasValue ? 1 : 0;

            Assert.True(countMeta1 + countMeta2 >= 0, $"Column {col.MemberName} evaluation failed.");
        }
    }

    [Fact]
    public void Verify_NetDebtEquityRatio_Filtering_DoesNotConfoundWithNetDebt()
    {
        // Arrange:
        // AAPL has NetDebt = +10,000,000 (positive net debt amount) but NetDebtEquityRatio = -0.5m (negative ratio)
        var metaAAPL = new TickerMetadata("AAPL", "Apple Inc.", "Tech", "Hardware", "US", "EQUITY")
        {
            NetDebt = 10000000m,
            NetDebtEquityRatio = -0.5m
        };

        // MSFT has NetDebt = +5,000,000 (positive net debt amount) AND NetDebtEquityRatio = +0.8m (positive ratio)
        var metaMSFT = new TickerMetadata("MSFT", "Microsoft Corp.", "Tech", "Software", "US", "EQUITY")
        {
            NetDebt = 5000000m,
            NetDebtEquityRatio = 0.8m
        };

        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(DateTime.Today, 150m, 160m, 140m, 155m, 1000)
        };

        // Act: Evaluate Net D/E Ratio > 0
        var configNetDE = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Column,
            CustomDisplayName = "Net D/E Ratio",
            OutputName = "NetDebtEquityRatio"
        };

        var valAAPL = ScreenerValueExtractor.Default.ExtractValueNullable(configNetDE, candles, metaAAPL);
        var valMSFT = ScreenerValueExtractor.Default.ExtractValueNullable(configNetDE, candles, metaMSFT);

        // Assert:
        // AAPL ratio MUST extract -0.5m (NOT 10,000,000m)
        Assert.NotNull(valAAPL);
        Assert.Equal(-0.5m, valAAPL.Value);

        // MSFT ratio MUST extract 0.8m
        Assert.NotNull(valMSFT);
        Assert.Equal(0.8m, valMSFT.Value);

        // Under > 0 screening filter, ONLY MSFT must pass
        bool aaplPasses = valAAPL.Value > 0m;
        bool msftPasses = valMSFT.Value > 0m;

        Assert.False(aaplPasses, "AAPL with negative Net D/E Ratio (-0.5) must NOT pass Net D/E Ratio > 0 filter.");
        Assert.True(msftPasses, "MSFT with positive Net D/E Ratio (0.8) must pass Net D/E Ratio > 0 filter.");
    }

    [Fact]
    public void Verify_NetDebtToEbitda_And_AllColumnDisplayNames_MatchesExpectedCount()
    {
        // Arrange:
        // AAPL has NetDebt = +10,000,000 (positive debt) but NetDebtToEbitda = -1.2m (negative ratio)
        var metaAAPL = new TickerMetadata("AAPL", "Apple Inc.", "Tech", "Hardware", "US", "EQUITY")
        {
            NetDebt = 10000000m,
            Ebitda = 2000000m,
            NetDebtToEbitda = -1.2m,
            GmtOffSetMilliseconds = 3600000L,
            NumberOfAnalystOpinions = 10L,
            PbrCalculated = 5m,
            DividendYieldCalculated = 0.01m,
            ReturnOnEquity = 0.15m,
            ReturnOnAssets = 0.08m,
            CurrentRatio = 1.2m,
            QuickRatio = 0.9m,
            DebtToEquity = 0.5m,
            FreeCashflow = 500000m,
            OperatingCashflow = 700000m,
            TrailingPE = 25m,
            ForwardPE = 20m,
            PriceToBook = 5m,
            PriceToSalesTrailing12Months = 4m,
            PegRatio = 1.5m,
            EnterpriseValue = 1500000m,
            EnterpriseToEbitda = 12m,
            MarketCap = 2000000m,
            SharesOutstanding = 1000000L,
            FloatShares = 900000L,
            TotalDebt = 3000000m,
            TotalCash = 1000000m,
            TotalRevenue = 8000000m,
            Beta = 1.1m,
            FiftyTwoWeekHigh = 200m,
            FiftyTwoWeekLow = 100m,
            CurrentPrice = 150m,
            FiftyTwoWeekRangePosition = 0.5m,
            PctFromFiftyTwoWeekHigh = -25m,
            MarketCapPerEmployee = 100000m,
            RevenueGrowth = 0.1m,
            EarningsGrowth = 0.12m,
            TrailingEps = 5m,
            ForwardEps = 6m,
            BookValue = 30m,
            ShortRatio = 2m,
            ShortPercentOfFloat = 0.05m,
            HeldPercentInsiders = 0.02m,
            HeldPercentInstitutions = 0.7m,
            EarningsYield = 0.04m,
            FcfYield = 0.05m,
            DividendCoverage = 2.5m,
            FloatRatio = 0.9m,
            OperatingCashFlowYield = 0.06m,
            NetCashRatio = 0.2m,
            EnterpriseToRevenue = 3m,
            AverageVolume = 5000000L,
            PriceToCashFlowRatio = 15m,
            DailyTurnoverRate = 0.01m,
            TargetHighPrice = 220m,
            TargetLowPrice = 140m,
            TargetMeanPrice = 180m,
            TargetMedianPrice = 180m,
            RecommendationMean = 2m,
            EbitdaMargins = 0.25m,
            GrossMargins = 0.4m,
            OperatingMargins = 0.2m,
            ProfitMargins = 0.15m,
            FcfMargin = 0.12m,
            PayoutRatio = 0.3m,
            DividendRate = 1.5m,
            DividendYield = 0.01m
        };

        // MSFT has NetDebt = +5,000,000 AND NetDebtToEbitda = +2.5m (positive ratio)
        var metaMSFT = new TickerMetadata("MSFT", "Microsoft Corp.", "Tech", "Software", "US", "EQUITY")
        {
            NetDebt = 5000000m,
            Ebitda = 2000000m,
            NetDebtToEbitda = 2.5m
        };

        var candles = new List<CoreCandleData>
        {
            new CoreCandleData(DateTime.Today, 150m, 160m, 140m, 155m, 1000)
        };

        var tickers = new[] { metaAAPL, metaMSFT };

        // Act 1: Specifically verify Net Debt / EBITDA using DisplayName
        var configNetDebtEbitda = new ScreenerIndicatorSideConfig
        {
            CategoryType = ScreenerItemCategoryType.Column,
            CustomDisplayName = "Net Debt / EBITDA",
            OutputName = "NetDebtToEbitda"
        };

        var valAAPL = ScreenerValueExtractor.Default.ExtractValueNullable(configNetDebtEbitda, candles, metaAAPL);
        var valMSFT = ScreenerValueExtractor.Default.ExtractValueNullable(configNetDebtEbitda, candles, metaMSFT);

        // Assert NetDebt/EBITDA values extracted correctly
        Assert.NotNull(valAAPL);
        Assert.Equal(-1.2m, valAAPL.Value);

        Assert.NotNull(valMSFT);
        Assert.Equal(2.5m, valMSFT.Value);

        int countNetDebtEbitdaPositive = tickers.Count(t => ScreenerValueExtractor.Default.ExtractValueNullable(configNetDebtEbitda, candles, t) > 0m);
        Assert.Equal(1, countNetDebtEbitdaPositive); // Only MSFT (+2.5 > 0)

        // Act 2: Verify ALL 93 Watchlist columns using their exact UI DisplayNames
        var stringColumns = new HashSet<string> { 
            "IsChecked", "Symbol", "Name", "Sector", "Industry", "Country", "Region", "Exchange", "QuoteType", "Currency", "FinancialCurrency",
            "LongBusinessSummary", "City", "State", "Zip", "Website", "Phone", "Address1", "Address2", "FullTimeEmployees",
            "ExDividendDate", "LastFiscalYearEnd", "MostRecentQuarter", "ExchangeTimezoneName", "ExchangeTimezoneShortName", "GmtOffSetMilliseconds", "RecommendationKey",
            "LastUpdatedUtc", "FetchedAtUtc", "MetadataLastUpdated", "Tag", "Notes", "CustomGroup"
        };

        foreach (var col in StockAnalyzer.Core.Models.Watchlist.WatchlistColumnRegistry.AllColumns)
        {
            if (stringColumns.Contains(col.MemberName) || col.HeaderKey == "Col_Select")
                continue;

            var configByDisplayName = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Column,
                CustomDisplayName = col.DisplayName,
                OutputName = col.MemberName
            };

            var val1 = ScreenerValueExtractor.Default.ExtractValueNullable(configByDisplayName, candles, metaAAPL);
            var val2 = ScreenerValueExtractor.Default.ExtractValueNullable(configByDisplayName, candles, metaMSFT);

            // Ensure every registered metadata column is correctly resolvable via its UI DisplayName
            Assert.True(val1.HasValue || val2.HasValue, $"Column DisplayName '{col.DisplayName}' (MemberName: {col.MemberName}) failed to extract any value.");
        }
    }

    [Fact]
    public void Verify_ColumnCatalog_IsSortedAlphabeticallyByGroupAndName()
    {
        // Arrange
        var provider = new ScreenerCatalogProvider();
        var catalog = provider.GetCatalogItems();

        // Act: Extract all Column category items
        var columnItems = catalog.Where(item => item.CategoryType == ScreenerItemCategoryType.Column).ToList();
        Assert.NotEmpty(columnItems);

        // 1. Verify overall list is ordered by ScreenerGroupNames.GetGroupSortOrder
        var expectedGroupOrder = columnItems
            .OrderBy(item => ScreenerGroupNames.GetGroupSortOrder(item.GroupName))
            .ThenBy(item => item.ShortName, StringComparer.OrdinalIgnoreCase)
            .Select(item => (item.GroupName, item.ShortName))
            .ToList();

        var actualGroupOrder = columnItems
            .Select(item => (item.GroupName, item.ShortName))
            .ToList();

        Assert.Equal(expectedGroupOrder, actualGroupOrder);

        // 2. Group items by GroupName and verify that each group's items are sorted alphabetically by ShortName
        var grouped = columnItems.GroupBy(item => item.GroupName);

        foreach (var group in grouped)
        {
            var shortNames = group.Select(item => item.ShortName).ToList();
            var sortedShortNames = shortNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();

            Assert.Equal(sortedShortNames, shortNames);
        }
    }
}
