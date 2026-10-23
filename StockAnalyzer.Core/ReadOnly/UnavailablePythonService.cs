using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.ReadOnly;

/// <summary>
/// Null Object that satisfies the <see cref="IPythonService"/> constructor parameter of
/// <see cref="ParquetMarketDataProvider"/> in the read-only composition. Every operation throws, so a
/// Python process can never be started through it; the no-op default members of the interface stay as they are.
/// </summary>
public sealed class UnavailablePythonService : IPythonService
{
    public static UnavailablePythonService Instance { get; } = new();

    private UnavailablePythonService()
    {
    }

    public bool IsInitializing => false;

    public Task InitializeAsync(IProgress<string>? progress = null, CancellationToken ct = default) => Unsupported<bool>();

    public Task InitializeExternalProcessAsync() => Unsupported<bool>();

    public Task<string> PingExternalProcessAsync() => Unsupported<string>();

    public Task<string> SendCandlesAsync(List<CandleData> candles) => Unsupported<string>();

    public Task<string> CalculateBacktestStatsAsync(IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades) => Unsupported<string>();

    public Task<string> CalculateFftTrendFilterAsync(int windowSize = IndicatorDefaultConstants.FftTrendFilterDefaultWindowSize, int numHarmonics = IndicatorDefaultConstants.FftTrendFilterDefaultNumHarmonics) => Unsupported<string>();

    public Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = IndicatorDefaultConstants.DtwDefaultWarpingRadius, IReadOnlyList<double>? volatility = null) => Unsupported<string>();

    public Task RunUpdatePipelineAsync(string? symbol = null, IProgress<int>? progress = null, bool forceMetadata = false, CancellationToken ct = default) => Unsupported<bool>();

    public Task<T> RunAsync<T>(Func<Python.Runtime.PyModule, T> func, CancellationToken cancellationToken = default) => Unsupported<T>();

    private static Task<T> Unsupported<T>()
        => Task.FromException<T>(new NotSupportedException("Python is not available in the read-only composition."));
}
