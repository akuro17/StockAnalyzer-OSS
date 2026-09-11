using System.Collections.Generic;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services
{
    public readonly record struct SyncSessionConfig(
        bool IsTimeSeriesSyncEnabled,
        bool IsMetadataSyncEnabled,
        bool IsAutoSyncEnabled,
        bool IsFullHistoryEnabled,
        decimal DelayMinSeconds,
        decimal DelayMaxSeconds,
        int StartSyncPeriodYears,
        bool IsForcePeriodDownloadEnabled = false,
        bool IsImputeMissingMetadataEnabled = false
    );

    public enum PythonSetupDecision
    {
        Automatic,
        Manual,
        Cancel
    }

    public interface IPythonService
    {
        bool IsInitializing { get; }
        System.Func<Task<PythonSetupDecision>>? SetupDecisionProvider { get => null; set { } }
        System.Func<Task<PythonSetupDecision>>? UpdateDecisionProvider { get => null; set { } }
        bool IsUpdateSuppressed { get => false; set { } }
        System.Action<System.IProgress<string>>? SetupProgressStarted { get => null; set { } }
        System.Action? SetupProgressFinished { get => null; set { } }
        Task InitializeAsync(System.IProgress<string>? progress = null, System.Threading.CancellationToken ct = default);
        Task InitializeExternalProcessAsync();
        Task<string> PingExternalProcessAsync();
        Task<string> SendCandlesAsync(List<CandleData> candles);
        Task<string> CalculateEgarchAsync(int p = 1, int q = 1);
        Task<string> CalculateMesaAsync(decimal fastLimit = 0.5m, decimal slowLimit = 0.05m);
        Task<string> CalculateFftCycleAsync(int windowSize = IndicatorDefaultConstants.FftCycleDefaultWindowSize);
        Task<string> CalculateFourierTransformAsync(int targetPeriod = IndicatorDefaultConstants.FourierTransformDefaultTargetPeriod);
        Task<string> CalculateFftTrendFilterAsync(int windowSize = IndicatorDefaultConstants.FftTrendFilterDefaultWindowSize, int numHarmonics = IndicatorDefaultConstants.FftTrendFilterDefaultNumHarmonics);
        Task<string> CalculateSsaAsync(int windowSize = IndicatorDefaultConstants.SsaDefaultWindowSize, int embeddingDimension = IndicatorDefaultConstants.SsaDefaultEmbeddingDimension, int numComponents = IndicatorDefaultConstants.SsaDefaultNumComponents, PriceType priceSource = PriceType.Close) => Task.FromResult("{}");
        Task<string> CalculateBacktestStatsAsync(IEnumerable<StockAnalyzer.Core.Models.Backtest.Trade> trades);
        Task<string> DetectPatternsAsync(int minWindow = 20, int maxWindow = 60, int windowStep = 5, double threshold = 0.5, int warpingRadius = IndicatorDefaultConstants.DtwDefaultWarpingRadius, double shortSpanPenaltyAlpha = ChartConstants.DtwShortSpanPenaltyAlpha);
        Task<string> CalculateStructuralDtwAsync(int topK = 5, double threshold = 0.3, int futureSteps = 20, int warpingRadius = IndicatorDefaultConstants.DtwDefaultWarpingRadius);
        Task<string> SearchSimilarPatternsAsync(int lookback = 0, int topK = 5, int futureSteps = 20, double threshold = 0.3, int queryLength = 30, int queryStartIndex = -1, bool useStructural = false, int warpingRadius = IndicatorDefaultConstants.DtwDefaultWarpingRadius);
        Task<string> CalculateStructuralDtwOscillatorAsync(int period = 14, int lag = 14, int warpingRadius = IndicatorDefaultConstants.DtwDefaultWarpingRadius);
        Task RunUpdatePipelineAsync(string? symbol = null, System.IProgress<int>? progress = null, bool forceMetadata = false, System.Threading.CancellationToken ct = default);
        Task RunUpdatePipelineAsync(string? symbol, SyncSessionConfig config, System.IProgress<int>? progress = null, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        Task<bool> IsPackageInstalledAsync(string packageName, System.Threading.CancellationToken ct = default) => Task.FromResult(false);
        Task InstallPackagesAsync(IEnumerable<string> packageNames, bool forceUpgrade = false, System.IProgress<string>? progress = null, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        Task RunPipCommandAsync(string arguments, System.Threading.CancellationToken ct = default) => Task.CompletedTask;

        /// <summary>
        /// Resolves the absolute path of the Python interpreter executable managed by this
        /// service (the embedded install set up by <see cref="InitializeAsync"/>), for launching
        /// standalone scripts outside the pythonnet IPC channel -- for example the training
        /// orchestrator (<c>StockAnalyzer.Python/training/run_training.py</c>). Ensures
        /// initialization has run first. The default implementation is for test doubles that do
        /// not model a real interpreter install.
        /// </summary>
        Task<string> ResolvePythonExecutablePathAsync(System.Threading.CancellationToken ct = default) =>
            Task.FromException<string>(new System.NotSupportedException(
                $"{nameof(ResolvePythonExecutablePathAsync)} is not supported by this {nameof(IPythonService)} implementation."));

        Task<T> RunAsync<T>(System.Func<Python.Runtime.PyModule, T> func, System.Threading.CancellationToken cancellationToken = default);
        Task<T> ExecuteTransactionAsync<T>(System.Func<Task<T>> action) => action();
    }
}
