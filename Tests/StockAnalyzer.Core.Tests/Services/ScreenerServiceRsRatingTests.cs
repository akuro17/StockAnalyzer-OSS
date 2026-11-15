using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ScreenerServiceRsRatingTests
{
    private sealed class StubSettings : IStockAnalyzerSettings
    {
        public string? PythonPath => null;
        public string PythonScriptDirectory => "Scripts";
        public string PythonServerScriptName => "server.py";
        public int PythonMaxRetries => 3;
        public int PythonBackoffMs => 1000;
        public int PythonHealthCheckIntervalMs => 5000;
        public string DefaultSymbol => "AAPL";
        public string RenkoUpColor => "Green";
        public string RenkoDownColor => "Red";
        public string KagiUpColor => "Green";
        public string KagiDownColor => "Red";
        public string PnfUpColor => "Green";
        public string PnfDownColor => "Red";
        public string? ScreeningDataPath => null;
        public string PipeName => "RsRatingTestPipe";
        public int PipeConnectionTimeoutMs { get; set; } = 5000;
        public int ScreenerMaxParallelism { get; set; } = 1;
        public int PatternRecognitionMinWindow => 20;
        public int PatternRecognitionMaxWindow => 60;
        public int PatternRecognitionWindowStep => 5;
        public double PatternRecognitionDefaultThreshold => 0.5;
        public decimal ZigzagThresholdPercent => 5.0m;
        public string? LocaleResourcePath => null;
        public string GetReverseWatchPhaseColor(int phase) => "#808080";
        public IReadOnlyList<string> DefaultScreenerSymbols => Array.Empty<string>();
        public int CircuitBreakerMinimumThroughput => 3;
        public double CircuitBreakerFailureRatio => 0.5;
        public int CircuitBreakerBreakDurationMs => 30000;
        public int CircuitBreakerSamplingDurationMs => 60000;
        public int PipeConnectPollIntervalMs => 100;
        public int DisposeWaitMs => 1000;
        public int SyncTimeoutMinutes => 2;
        public IReadOnlyList<string> PythonEssentialPackages => Array.Empty<string>();
        public string PredictionModelPath => "Models/trend_predictor.onnx";
        public int PredictionWindowSize => 10;
        public PredictionFeatureMode PredictionFeatureMode => PredictionFeatureMode.OhlcvMinMax;
        public float PredictionConfidenceThreshold => 0.5f;
        public string? PredictionInputNodeName => null;
        public string? PredictionOutputNodeName => null;
        public IReadOnlyList<string> PredictionClassLabels => new[] { "Up", "Down", "Neutral" };
        public int PredictionRetryMaxAttempts => 3;
        public int PredictionRetryBaseDelayMs => 50;
        public int PredictionRetryMaxDelayMs => 500;
    }

    private sealed class InMemoryDataService : IDataService
    {
        private readonly Dictionary<string, IReadOnlyList<CandleData>> _candles;

        public InMemoryDataService(Dictionary<string, IReadOnlyList<CandleData>> candles)
        {
            _candles = candles;
        }

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            if (_candles.TryGetValue(symbol, out var list))
            {
                return Task.FromResult<IReadOnlyList<CandleData>>(list.TakeLast(count).ToList());
            }

            return Task.FromResult<IReadOnlyList<CandleData>>(Array.Empty<CandleData>());
        }

        public Task<IReadOnlyList<CandleData>> LoadAllCandlesAsync(string symbol, TimeFrame timeFrame)
        {
            return Task.FromResult(_candles.TryGetValue(symbol, out var list) ? list : (IReadOnlyList<CandleData>)Array.Empty<CandleData>());
        }

        public Task SaveCandlesAsync(string symbol, TimeFrame timeFrame, IEnumerable<CandleData> candles)
            => Task.CompletedTask;

        public Task AppendCandlesAsync(string symbol, TimeFrame timeFrame, IEnumerable<CandleData> candles)
            => Task.CompletedTask;

        public Task<bool> DeleteDataAsync(string symbol, TimeFrame timeFrame)
            => Task.FromResult(false);

        public Task<IReadOnlyList<string>> GetAvailableSymbolsAsync(TimeFrame timeFrame)
            => Task.FromResult<IReadOnlyList<string>>(_candles.Keys.ToList());

        public Task<DateTime?> GetLastTimestampAsync(string symbol, TimeFrame timeFrame)
            => Task.FromResult<DateTime?>(_candles.TryGetValue(symbol, out var list) && list.Count > 0 ? list[^1].Timestamp : null);

        public Task<DateTimeOffset?> GetLastUpdatedAsync(string symbol, TimeFrame timeFrame)
            => Task.FromResult<DateTimeOffset?>(null);
    }

    private sealed class RecordingRsRatingClosesProvider : IRsRatingClosesProvider
    {
        private readonly IReadOnlyDictionary<string, RsRatingCloses> _closes;
        private readonly bool _shouldThrow;

        public RecordingRsRatingClosesProvider(IReadOnlyDictionary<string, RsRatingCloses> closes, bool shouldThrow = false)
        {
            _closes = closes;
            _shouldThrow = shouldThrow;
        }

        public int CallCount { get; private set; }
        public IReadOnlyList<string>? LastUniverse { get; private set; }

        public Task<IReadOnlyDictionary<string, RsRatingCloses>> GetRsRatingClosesAsync(
            IReadOnlyList<string> symbols,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastUniverse = symbols.ToList();
            if (_shouldThrow)
            {
                throw new InvalidOperationException("Simulated DuckDB error");
            }

            var filtered = new Dictionary<string, RsRatingCloses>(StringComparer.OrdinalIgnoreCase);
            foreach (var symbol in symbols)
            {
                if (_closes.TryGetValue(symbol, out var value))
                {
                    filtered[symbol] = value;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, RsRatingCloses>>(filtered);
        }
    }

    private static List<CandleData> MakeDailyBars(DateTime startDate, int count, decimal closePrice = 100m)
    {
        var bars = new List<CandleData>(count);
        for (int i = 0; i < count; i++)
        {
            bars.Add(new CandleData(startDate.AddDays(i), closePrice, closePrice + 1m, closePrice - 1m, closePrice, 1000L));
        }

        return bars;
    }

    private static ScreenerIndicatorEntry CreateRsRatingConditionEntry(decimal threshold)
    {
        return new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Column,
                CustomDisplayName = ScreenerRsRatingInspector.ColumnDisplayName,
                OutputName = ScreenerRsRatingInspector.ColumnMemberName
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = threshold,
            IsEnabled = true
        };
    }

    private static ScreenerConditionTreeCondition WrapInTree(ScreenerIndicatorEntry entry)
    {
        var root = new ScreenerConditionGroup(
            LogicalOperator.And,
            ImmutableArray.Create<IScreenerConditionNode>(new ScreenerConditionLeaf(entry)));
        return new ScreenerConditionTreeCondition(root);
    }

    [Fact]
    public void WatchlistColumnRegistry_ContainsRsRatingMetadataAtOrder108()
    {
        var rsColumn = WatchlistColumnRegistry.AllColumns.SingleOrDefault(c => c.MemberName == "RsRating");
        Assert.NotNull(rsColumn);
        Assert.Equal("Col_RsRating", rsColumn!.HeaderKey);
        Assert.Equal("RS Rating", rsColumn.DisplayName);
        Assert.Equal(108, rsColumn.Priority);
        Assert.Equal("PriceVolume", rsColumn.Category);
    }

    [Fact]
    public async Task ScreenAsync_OnDemand_DoesNotQueryProvider_WhenRsRatingIsNotUsed()
    {
        var bars = MakeDailyBars(new DateTime(2024, 1, 1), 30, 150m);
        var dataService = new InMemoryDataService(new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = bars,
            ["MSFT"] = bars
        });
        var provider = new RecordingRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(200m, 100m, 100m, 100m, 100m),
            ["MSFT"] = new(100m, 100m, 100m, 100m, 100m)
        });
        var screener = new ScreenerService(dataService, new StubSettings(), Array.Empty<IMarketDataProvider>(), rsRatingClosesProvider: provider);

        var closeCondition = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Column,
                OutputName = "Close"
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 100m,
            IsEnabled = true
        };

        var matched = await screener.ScreenAsync(
            new List<string> { "AAPL", "MSFT" },
            WrapInTree(closeCondition),
            TimeFrame.D1,
            new Progress<int>(),
            CancellationToken.None);

        Assert.Equal(2, matched.Count);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task ScreenAsync_RsRatingCondition_QueriesProviderOnceAcrossFullUniverse()
    {
        var bars = MakeDailyBars(new DateTime(2024, 1, 1), 30, 150m);
        var dataService = new InMemoryDataService(new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = bars,
            ["MSFT"] = bars,
            ["NVDA"] = bars
        });
        var provider = new RecordingRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(150m, 100m, 100m, 100m, 100m), // mid -> 50
            ["MSFT"] = new(90m, 100m, 100m, 100m, 100m),  // lowest -> 1
            ["NVDA"] = new(250m, 100m, 100m, 100m, 100m)  // highest -> 99
        });
        var screener = new ScreenerService(dataService, new StubSettings(), Array.Empty<IMarketDataProvider>(), rsRatingClosesProvider: provider);

        var matched = await screener.ScreenAsync(
            new List<string> { "AAPL", "MSFT", "NVDA" },
            WrapInTree(CreateRsRatingConditionEntry(40m)),
            TimeFrame.D1,
            new Progress<int>(),
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(new[] { "AAPL", "MSFT", "NVDA" }, provider.LastUniverse);
        Assert.Equal(new[] { "AAPL", "NVDA" }, matched.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ScreenOnBarsAsync_WithAsOfDate_DoesNotQueryProviderAndReturnsNullRsRating()
    {
        var bars = MakeDailyBars(new DateTime(2024, 1, 1), 300, 150m);
        var dataService = new InMemoryDataService(new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = bars,
            ["NVDA"] = bars
        });
        var provider = new RecordingRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(150m, 100m, 100m, 100m, 100m),
            ["NVDA"] = new(250m, 100m, 100m, 100m, 100m)
        });
        var screener = new ScreenerService(dataService, new StubSettings(), Array.Empty<IMarketDataProvider>(), rsRatingClosesProvider: provider);

        var selection = new ScreenerBarSelection(new DateOnly(2024, 6, 1), CompleteBarsOnly: true);
        var result = await screener.ScreenOnBarsAsync(
            new List<string> { "AAPL", "NVDA" },
            WrapInTree(CreateRsRatingConditionEntry(0m)),
            TimeFrame.D1,
            selection,
            new Progress<int>(),
            CancellationToken.None);

        Assert.Equal(0, provider.CallCount);
        Assert.Empty(result.Matched);
    }

    [Fact]
    public async Task ScreenerTemplateEvaluator_UsesRsRating_ComputesContextOnceAcrossEnabledEntries()
    {
        var bars = MakeDailyBars(new DateTime(2024, 1, 1), 30, 150m);
        var dataService = new InMemoryDataService(new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = bars,
            ["MSFT"] = bars
        });
        var provider = new RecordingRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>
        {
            ["AAPL"] = new(250m, 100m, 100m, 100m, 100m), // 99
            ["MSFT"] = new(80m, 100m, 100m, 100m, 100m)   // 1
        });
        var screener = new ScreenerService(dataService, new StubSettings(), Array.Empty<IMarketDataProvider>(), rsRatingClosesProvider: provider);
        var evaluator = new ScreenerTemplateEvaluator(screener, provider);

        var closeAbove100 = new ScreenerIndicatorEntry
        {
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Column,
                OutputName = "Close"
            },
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 100m,
            LogicalOperator = LogicalOperator.And,
            IsEnabled = true
        };
        var rsAbove50 = CreateRsRatingConditionEntry(50m);

        var template = new ScreenerIndicatorTemplate { Id = Guid.NewGuid(), Name = "RS Template" };
        template.SetEntries(new[] { closeAbove100, rsAbove50 });

        var matched = await evaluator.EvaluateAsync(
            new List<string> { "AAPL", "MSFT" },
            template,
            TimeFrame.D1,
            progress: null,
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(new[] { "AAPL" }, matched.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ScreenAsync_ProviderThrows_DegradesToNullWithoutThrowing()
    {
        var bars = MakeDailyBars(new DateTime(2024, 1, 1), 30, 150m);
        var dataService = new InMemoryDataService(new Dictionary<string, IReadOnlyList<CandleData>>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = bars
        });
        var provider = new RecordingRsRatingClosesProvider(new Dictionary<string, RsRatingCloses>(), shouldThrow: true);
        var screener = new ScreenerService(dataService, new StubSettings(), Array.Empty<IMarketDataProvider>(), rsRatingClosesProvider: provider);

        var matched = await screener.ScreenAsync(
            new List<string> { "AAPL" },
            WrapInTree(CreateRsRatingConditionEntry(0m)),
            TimeFrame.D1,
            new Progress<int>(),
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Empty(matched);
    }
}
