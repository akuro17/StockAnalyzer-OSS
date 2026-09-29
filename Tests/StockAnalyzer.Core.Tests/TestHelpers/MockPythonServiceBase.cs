using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// Shared base implementation of IPythonService for unit tests.
/// Provides default no-op/stub implementations for all methods.
/// Override specific methods in test classes to customize behavior.
/// </summary>
public class MockPythonServiceBase : IPythonService
{
    public virtual bool IsInitializing => false;

    public virtual Task InitializeAsync(IProgress<string>? progress = null, CancellationToken ct = default) => Task.CompletedTask;

    public virtual Task InitializeExternalProcessAsync() => Task.CompletedTask;

    public virtual Task<string> PingExternalProcessAsync() => Task.FromResult("pong");

    public virtual Task<string> SendCandlesAsync(List<CandleData> candles)
        => Task.FromResult("{\"status\":\"transfer_complete\",\"rows\":" + candles.Count + "}");

    public virtual Task<string> CalculateFftTrendFilterAsync(int windowSize = ChartConstants.FftTrendFilterDefaultWindowSize, int numHarmonics = ChartConstants.FftTrendFilterDefaultNumHarmonics)
        => Task.FromResult("{}");

    public virtual Task<string> CalculateBacktestStatsAsync(IEnumerable<Trade> trades)
        => Task.FromResult("{}");

    public virtual Task<string> SearchSimilarPatternsAsync(
        int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3,
        int queryLength = 30, int queryStartIndex = -1, bool useStructural = false,
        int warpingRadius = ChartConstants.DtwDefaultWarpingRadius, IReadOnlyList<double>? volatility = null)
        => Task.FromResult("{}");


    public virtual Task RunUpdatePipelineAsync(string? symbol = null, IProgress<int>? progress = null, bool forceMetadata = false, CancellationToken ct = default)
        => Task.CompletedTask;

    public virtual Task<T> RunAsync<T>(
        Func<Python.Runtime.PyModule, T> func,
        CancellationToken cancellationToken = default)
        => Task.FromResult(default(T)!);
}
