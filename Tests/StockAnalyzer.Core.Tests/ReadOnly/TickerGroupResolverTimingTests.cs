using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Templates;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using Xunit;

namespace StockAnalyzer.Core.Tests.ReadOnly;

public sealed class TickerGroupResolverTimingTests
{
    private sealed class FastWorkspaceSource : IReadOnlyWorkspaceSource
    {
        private readonly WorkspaceSettings _settings;
        public FastWorkspaceSource(WorkspaceSettings settings) => _settings = settings;
        public Task<WorkspaceSettings?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<WorkspaceSettings?>(_settings);
    }

    private sealed class FastGateway : IReadOnlyStockAnalyzerGateway
    {
        private readonly List<string> _registry;
        private readonly TickerMetadata _metadata;
        private readonly List<CandleData> _candles;

        public FastGateway(int symbolCount)
        {
            _registry = Enumerable.Range(1, symbolCount).Select(i => $"SYM{i:D4}").ToList();
            _metadata = new TickerMetadata
            {
                ShortName = "Synthetic",
                Sector = "Technology",
                Industry = "Software",
                TrailingEps = 5.0m,
                SharesOutstanding = 1_000_000m
            };

            var d1 = new DateTime(2026, 1, 1);
            var d2 = new DateTime(2026, 1, 2);
            _candles = new List<CandleData>
            {
                new(d1, 100m, 105m, 99m, 102m, 10_000),
                new(d2, 102m, 110m, 101m, 108m, 12_000)
            };
        }

        public Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_registry);

        public Task<TickerMetadata?> GetStoredTickerMetadataAsync(string symbol, CancellationToken cancellationToken = default)
            => Task.FromResult<TickerMetadata?>(_metadata);

        public Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CandleData>>(_candles);

        public Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<Dictionary<string, IIndicatorResult>> CalculateIndicatorsAsync(IReadOnlyList<CoreCandleData> candles, IEnumerable<CoreIndicatorSettings> settings, IReadOnlyDictionary<string, IReadOnlyList<CoreCandleData?>>? secondaryCandlesBySymbol = null) => throw new NotImplementedException();
        public ReverseWatchCurveData? CalculateReverseWatch(IReadOnlyList<CoreCandleData> candles, int period, string symbol, bool isMaBased = true, bool isLogScaleVolume = false, int dataCount = 0) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RunScreenerAsync(IReadOnlyList<string> symbols, IScreeningCondition condition, TimeFrame timeFrame, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ScreenerTemplateListing> ListScreenerTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ScreenerIndicatorTemplate?> GetScreenerTemplateAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BacktestConditionTemplateListing> ListBacktestConditionTemplatesAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BacktestConditionTemplate?> GetBacktestConditionTemplateAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RunScreenerTemplateAsync(IReadOnlyList<string> symbols, ScreenerIndicatorTemplate template, TimeFrame timeFrame, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BacktestEvaluationArtifact> RunBacktestAsync(BacktestRunSnapshot snapshot, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }

    [Fact]
    public async Task TimingGate_1000SymbolsFilterEvaluation_CompletesWithin15Seconds()
    {
        const int SymbolCount = 1000;
        var filterId = Guid.NewGuid();
        var workspace = new WorkspaceSettings
        {
            TagFilters = new List<FilterSettings>
            {
                new()
                {
                    Id = filterId,
                    ParentId = TickerGroupIds.AllTickers,
                    Name = "Timing Filter",
                    Rules = new List<FilterRule>
                    {
                        new() { Field = "Close", Operator = ">", Value = "50" },
                        new() { Field = "Sector", Operator = "==", Value = "Technology" },
                        new() { Field = "TrailingPE", Operator = "<", Value = "30" }
                    }
                }
            }
        };

        var gateway = new FastGateway(SymbolCount);
        var resolver = new TickerGroupResolver(gateway, new FastWorkspaceSource(workspace));

        var sw = Stopwatch.StartNew();
        var result = await resolver.ResolveAsync(filterId, maxFilterEvaluationSymbols: SymbolCount);
        sw.Stop();

        Assert.Equal(SymbolCount, result.Symbols.Count);
        // Timing gate: Must finish within 15 seconds (ToolTimeoutSeconds / 2)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"1,000-symbol filter evaluation took {sw.ElapsedMilliseconds} ms, exceeding 15 s.");
    }
}
