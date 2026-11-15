#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Statistics;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Watchlist;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Analysis;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Screener;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ScreenerServiceUniverseBreadthTests
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
        public string PipeName => "UniverseBreadthTestPipe";
        public int PipeConnectionTimeoutMs { get; set; } = 5000;
        public int ScreenerMaxParallelism { get; set; } = 4;
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

    private sealed class SharedHistoryDataService : IDataService
    {
        private readonly IReadOnlyList<CandleData> _templateBars;
        private readonly IReadOnlyList<CandleData> _shorterBars;
        private readonly HashSet<string> _symbolsWithShorterHistory;

        public SharedHistoryDataService(
            IReadOnlyList<CandleData> templateBars,
            IReadOnlyList<CandleData> shorterBars,
            HashSet<string> symbolsWithShorterHistory)
        {
            _templateBars = templateBars;
            _shorterBars = shorterBars;
            _symbolsWithShorterHistory = symbolsWithShorterHistory;
        }

        public Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            IReadOnlyList<CandleData> source = _symbolsWithShorterHistory.Contains(symbol) ? _shorterBars : _templateBars;
            return Task.FromResult<IReadOnlyList<CandleData>>(count > 0 && source.Count > count ? source.Skip(source.Count - count).ToList() : source);
        }

        public Task<IReadOnlyList<CandleData>> LoadAllCandlesAsync(string symbol, TimeFrame timeFrame)
            => Task.FromResult(_symbolsWithShorterHistory.Contains(symbol) ? _shorterBars : _templateBars);

        public Task SaveCandlesAsync(string symbol, TimeFrame timeFrame, IEnumerable<CandleData> candles) => Task.CompletedTask;
        public Task AppendCandlesAsync(string symbol, TimeFrame timeFrame, IEnumerable<CandleData> candles) => Task.CompletedTask;
        public Task<bool> DeleteDataAsync(string symbol, TimeFrame timeFrame) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> GetAvailableSymbolsAsync(TimeFrame timeFrame) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<DateTime?> GetLastTimestampAsync(string symbol, TimeFrame timeFrame) => Task.FromResult<DateTime?>(null);
        public Task<DateTimeOffset?> GetLastUpdatedAsync(string symbol, TimeFrame timeFrame) => Task.FromResult<DateTimeOffset?>(null);
    }

    private sealed class CountingBreadthProvider : IMarketBreadthSeriesProvider
    {
        private readonly Func<Guid, TimeFrame, MarketBreadthParameters, MarketBreadthResult?> _factory;

        public int CallCount => Volatile.Read(ref _callCount);
        private int _callCount;
        public List<(Guid GroupId, TimeFrame TimeFrame, MarketBreadthParameters Parameters)> Calls { get; } = new();

        public CountingBreadthProvider(Func<Guid, TimeFrame, MarketBreadthParameters, MarketBreadthResult?> factory)
        {
            _factory = factory;
        }

        public Task<MarketBreadthResult?> GetAsync(Guid groupId, TimeFrame timeFrame, MarketBreadthParameters parameters, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            lock (Calls)
            {
                Calls.Add((groupId, timeFrame, parameters));
            }

            return Task.FromResult(_factory(groupId, timeFrame, parameters));
        }

        public void ClearCache()
        {
        }
    }

    private static List<CandleData> BuildDailyBars(DateTime start, int count, Func<int, decimal> closeSelector)
    {
        var list = new List<CandleData>(count);
        DateTime d = start;
        while (list.Count < count)
        {
            if (d.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday)
            {
                decimal close = closeSelector(list.Count);
                list.Add(new CandleData(d, close, close + 1m, close - 1m, close, 1000L));
            }

            d = d.AddDays(1);
        }

        return list;
    }

    [Fact]
    public async Task ScreenAsync_500SymbolsWithMarketBreadthCondition_CallsProviderGetAsyncOnce()
    {
        Guid groupId = Guid.NewGuid();
        var fullBars = BuildDailyBars(new DateTime(2024, 1, 1), 60, i => 100m + i);
        // Shorter history ending before breadth warmup completes -> evaluates to null (no match), while fullBars matches.
        var shortBars = fullBars.Take(2).ToList();

        var universe = new Dictionary<string, IReadOnlyList<CandleData>>
        {
            ["U1"] = BuildDailyBars(new DateTime(2024, 1, 1), 60, i => 50m + i), // always advancing -> PctAboveMa = 100%
            ["U2"] = BuildDailyBars(new DateTime(2024, 1, 1), 60, i => 60m + i)
        };

        var breadthProvider = new CountingBreadthProvider((_, tf, p) =>
            MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters
            {
                MaBars = p.MaBars,
                TimeFrame = tf
            }));

        var symbols = Enumerable.Range(1, 500).Select(i => $"SYM{i:D3}").ToList();
        var missingTailSymbols = new HashSet<string>(symbols.Where((_, idx) => idx % 2 == 1), StringComparer.OrdinalIgnoreCase);

        var service = new ScreenerService(
            new SharedHistoryDataService(fullBars, shortBars, missingTailSymbols),
            new StubSettings(),
            Array.Empty<IMarketDataProvider>(),
            marketBreadthProvider: breadthProvider);

        var entry = new ScreenerIndicatorEntry
        {
            IsEnabled = true,
            CategoryType = ScreenerItemCategoryType.Indicator,
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 50m,
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Indicator,
                IndicatorType = IndicatorType.MarketBreadth,
                TimeFrame = TimeFrame.D1,
                Parameters = new Dictionary<string, object>
                {
                    ["UniverseGroupId"] = groupId.ToString("D"),
                    ["MetricMode"] = MarketBreadthMetricMode.PctAboveMa.ToString(),
                    ["MaBars"] = 10
                }
            }
        };

        var condition = new ScreenerConditionTreeCondition(
            new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(new ScreenerConditionLeaf(entry))));

        List<string> matched = await service.ScreenAsync(symbols, condition, TimeFrame.D1, new Progress<int>(_ => { }), CancellationToken.None);

        Assert.Equal(1, breadthProvider.CallCount);
        Assert.Equal(250, matched.Count);
        Assert.All(matched, s => Assert.DoesNotContain(s, missingTailSymbols));
    }

    [Fact]
    public async Task ScreenAsync_TwoDistinctBreadthKeys_CallsProviderOncePerDistinctKey()
    {
        Guid groupA = Guid.NewGuid();
        Guid groupB = Guid.NewGuid();
        var bars = BuildDailyBars(new DateTime(2024, 1, 1), 50, i => 100m + i);
        var universe = new Dictionary<string, IReadOnlyList<CandleData>> { ["U1"] = bars };

        var provider = new CountingBreadthProvider((_, tf, p) =>
            MarketBreadthCalculator.Calculate(universe, new MarketBreadthParameters
            {
                McClellanFast = p.McClellanFast,
                McClellanSlow = p.McClellanSlow,
                AdRatioBars = p.AdRatioBars,
                TimeFrame = tf
            }));

        var symbols = Enumerable.Range(1, 20).Select(i => $"T{i}").ToList();
        var service = new ScreenerService(
            new SharedHistoryDataService(bars, bars, new HashSet<string>()),
            new StubSettings(),
            Array.Empty<IMarketDataProvider>(),
            marketBreadthProvider: provider);

        var leaf1 = new ScreenerConditionLeaf(new ScreenerIndicatorEntry
        {
            IsEnabled = true,
            CategoryType = ScreenerItemCategoryType.Indicator,
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = -1000m,
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Indicator,
                IndicatorType = IndicatorType.McClellanOscillator,
                TimeFrame = TimeFrame.D1,
                Parameters = new Dictionary<string, object> { ["UniverseGroupId"] = groupA.ToString("D"), ["FastPeriod"] = 5, ["SlowPeriod"] = 10 }
            }
        });

        var leaf2 = new ScreenerConditionLeaf(new ScreenerIndicatorEntry
        {
            IsEnabled = true,
            CategoryType = ScreenerItemCategoryType.Indicator,
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 0m,
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Indicator,
                IndicatorType = IndicatorType.MarketBreadth,
                TimeFrame = TimeFrame.D1,
                Parameters = new Dictionary<string, object> { ["UniverseGroupId"] = groupB.ToString("D"), ["MetricMode"] = "AdLine" }
            }
        });

        // Duplicate leaf with same key as leaf1 should not cause a third GetAsync call.
        var leaf1Duplicate = new ScreenerConditionLeaf(new ScreenerIndicatorEntry
        {
            IsEnabled = true,
            CategoryType = ScreenerItemCategoryType.Indicator,
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = -500m,
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Indicator,
                IndicatorType = IndicatorType.McClellanOscillator,
                OutputName = CoreMcClellanOscillatorIndicator.SummationIndexSeriesName,
                TimeFrame = TimeFrame.D1,
                Parameters = new Dictionary<string, object> { ["UniverseGroupId"] = groupA.ToString("D"), ["FastPeriod"] = 5, ["SlowPeriod"] = 10 }
            }
        });

        var tree = new ScreenerConditionTreeCondition(
            new ScreenerConditionGroup(LogicalOperator.And, ImmutableArray.Create<IScreenerConditionNode>(leaf1, leaf2, leaf1Duplicate)));

        List<string> matched = await service.ScreenAsync(symbols, tree, TimeFrame.D1, new Progress<int>(_ => { }), CancellationToken.None);

        Assert.Equal(2, provider.CallCount);
        Assert.Equal(symbols.Count, matched.Count);
    }

    [Fact]
    public void ScreenerAndBacktestEligibility_MatchPlan10ContractForUniverseBreadthIndicators()
    {
        Assert.True(ScreenerIndicatorEligibility.IsUniverseBreadthType(IndicatorType.McClellanOscillator));
        Assert.True(ScreenerIndicatorEligibility.IsUniverseBreadthType(IndicatorType.MarketBreadth));
        Assert.False(ScreenerIndicatorEligibility.IsTwoTickerType(IndicatorType.McClellanOscillator));
        Assert.False(ScreenerIndicatorEligibility.IsTwoTickerType(IndicatorType.MarketBreadth));

        Assert.True(ScreenerIndicatorEligibility.IsAllowed(IndicatorType.McClellanOscillator));
        Assert.True(ScreenerIndicatorEligibility.IsAllowed(IndicatorType.MarketBreadth));
        Assert.False(ScreenerIndicatorEligibility.IsTypeBlocked(IndicatorType.McClellanOscillator));
        Assert.False(ScreenerIndicatorEligibility.IsTypeBlocked(IndicatorType.MarketBreadth));
        Assert.True(ScreenerIndicatorEligibility.IsScreenerOnlyType(IndicatorType.McClellanOscillator));
        Assert.True(ScreenerIndicatorEligibility.IsScreenerOnlyType(IndicatorType.MarketBreadth));

        Assert.True(BacktestIndicatorEligibility.IsTypeBlocked(IndicatorType.McClellanOscillator));
        Assert.True(BacktestIndicatorEligibility.IsTypeBlocked(IndicatorType.MarketBreadth));
    }

    [Fact]
    public async Task ScreenOnBarsAsync_WithAsOfDate_ForwardsEndDateToBreadthProvider_AndPreservesBreadthWhileBlockingRsRating()
    {
        Guid groupId = Guid.NewGuid();
        var bars = BuildDailyBars(new DateTime(2024, 1, 1), 60, i => 100m + i);
        var universe = new Dictionary<string, IReadOnlyList<CandleData>> { ["U1"] = bars };
        DateOnly? observedEndDate = null;

        var provider = new CountingBreadthProvider((_, tf, p) =>
        {
            observedEndDate = p.EndDate;
            IReadOnlyList<CandleData> trimmed = p.EndDate is { } cutoff
                ? AsOfCandleWindow.Build(bars, cutoff, tf, count: 0)
                : bars;
            return MarketBreadthCalculator.Calculate(
                new Dictionary<string, IReadOnlyList<CandleData>> { ["U1"] = trimmed },
                new MarketBreadthParameters
                {
                    McClellanFast = p.McClellanFast,
                    McClellanSlow = p.McClellanSlow,
                    AdRatioBars = p.AdRatioBars,
                    TimeFrame = tf,
                    EndDate = p.EndDate
                });
        });

        var service = new ScreenerService(
            new SharedHistoryDataService(bars, bars, new HashSet<string>()),
            new StubSettings(),
            Array.Empty<IMarketDataProvider>(),
            marketBreadthProvider: provider);

        var entry = new ScreenerIndicatorEntry
        {
            IsEnabled = true,
            CategoryType = ScreenerItemCategoryType.Indicator,
            Operator = ComparisonOperator.GreaterThan,
            TargetMode = RightHandTargetMode.NumericValue,
            RightNumericValue = 0m,
            LeftHand = new ScreenerIndicatorSideConfig
            {
                CategoryType = ScreenerItemCategoryType.Indicator,
                IndicatorType = IndicatorType.MarketBreadth,
                TimeFrame = TimeFrame.D1,
                Parameters = new Dictionary<string, object> { ["UniverseGroupId"] = groupId.ToString("D"), ["MetricMode"] = "AdLine" }
            }
        };

        var asOf = new DateOnly(2024, 2, 14);
        var selection = new ScreenerBarSelection(AsOfDate: asOf, CompleteBarsOnly: false);

        ScreenerBarsResult result = await service.ScreenOnBarsAsync(
            new List<string> { "AAPL", "MSFT" },
            entry,
            TimeFrame.W1,
            selection,
            new Progress<int>(_ => { }),
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(asOf, observedEndDate);
        Assert.Equal(2, result.Matched.Count);

        IScreenerEvaluationContext asOfContext = ScreenerEvaluationContext.ForAsOfDate(asOf, new ScreenerEvaluationContext(new Dictionary<string, int> { ["AAPL"] = 99 }));
        Assert.Null(asOfContext.GetRsRating("AAPL"));
        Assert.Equal(asOf, asOfContext.AsOfDate);
    }
}
