using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Services;
using Python.Runtime;

namespace StockAnalyzer.Core.Tests.Services
{
    [Collection("PythonIpc")]
    public class PythonServiceTest : IAsyncLifetime
    {
        private PythonService _pythonService;

        public async Task InitializeAsync()
        {
            _pythonService = new PythonService(new DummySettings());
            await _pythonService.InitializeAsync();
        }

        private class DummySettings : IStockAnalyzerSettings
        {
            public string? PythonPath => null;
            public string PythonScriptDirectory => "";
            public string PythonServerScriptName => "";
            public int PythonMaxRetries => 3;
            public int PythonBackoffMs => 100;
            public int PythonHealthCheckIntervalMs => 1000;
            public int PipeConnectPollIntervalMs => 100;
            public int SyncTimeoutMinutes => 1;
            public IReadOnlyList<string> PythonEssentialPackages => new List<string>().AsReadOnly();
            public int DisposeWaitMs => 100;
            public string DefaultSymbol => "MSFT";
            public string RenkoUpColor => "#00FF00";
            public string RenkoDownColor => "#FF0000";
            public string KagiUpColor => "#00FF00";
            public string KagiDownColor => "#FF0000";
            public string PnfUpColor => "#00FF00";
            public string PnfDownColor => "#FF0000";
            public string GetReverseWatchPhaseColor(int phase) => "#FFFFFF";
            public string? ScreeningDataPath => null;
            public IReadOnlyList<string> DefaultScreenerSymbols => new List<string>().AsReadOnly();
            public string PipeName => "testpipe";
            public int PipeConnectionTimeoutMs => 1000;
            public int ScreenerMaxParallelism => 4;
            public decimal ZigzagThresholdPercent => 5m;
            public int PatternRecognitionMinWindow => 10;
            public int PatternRecognitionMaxWindow => 100;
            public int PatternRecognitionWindowStep => 5;
            public double PatternRecognitionDefaultThreshold => 0.5;
            public int CircuitBreakerMinimumThroughput => 10;
            public double CircuitBreakerFailureRatio => 0.5;
            public int CircuitBreakerBreakDurationMs => 1000;
            public int CircuitBreakerSamplingDurationMs => 1000;
            public string PredictionModelPath => "";
            public int PredictionWindowSize => 30;
            public StockAnalyzer.Core.Models.PredictionFeatureMode PredictionFeatureMode => StockAnalyzer.Core.Models.PredictionFeatureMode.OhlcvMinMax;
            public float PredictionConfidenceThreshold => 0.5f;
            public string? PredictionInputNodeName => null;
            public string? PredictionOutputNodeName => null;
            public System.Collections.Generic.IReadOnlyList<string> PredictionClassLabels => new[] { "Up", "Down", "Neutral" };
            public int PredictionRetryMaxAttempts => 3;
            public int PredictionRetryBaseDelayMs => 50;
            public int PredictionRetryMaxDelayMs => 500;
            public string? LocaleResourcePath => null;
        }

        public Task DisposeAsync()
        {
            // Do not dispose PythonService (and thus PythonEngine) between tests.
            // Python.NET engine is designed to be initialized once.
            // Repeated Initialize/Shutdown cycles are unstable.
            return Task.CompletedTask;
        }

        [Fact]
        public void Run_ShouldExecutePythonCode()
        {
            _pythonService.Run(scope =>
            {
                scope.Exec("a = 1 + 1");
                var result = scope.Eval("a");
                Assert.Equal(2, result.As<int>());
            });
        }

        [Fact]
        public void TestScipyInstallation()
        {
            _pythonService.Run(scope =>
            {
                scope.Exec("import scipy");
                scope.Exec("import scipy.signal");
            });
        }

        [Fact]
        public void Run_ShouldReturnResult()
        {
            int result = _pythonService.Run(scope =>
            {
                return scope.Eval("10 * 10").As<int>();
            });

            Assert.Equal(100, result);
        }

        [Fact]
        public void Run_ShouldHandlePythonExceptions()
        {
            Assert.Throws<PythonException>(() =>
            {
                _pythonService.Run(scope =>
                {
                    scope.Exec("1 / 0");
                });
            });
        }

        [Fact]
        public async Task RunUpdatePipelineAsync_WithInvalidConfig_ShouldThrowArgumentOutOfRangeException()
        {
            var invalidDelayConfig = new SyncSessionConfig(
                IsTimeSeriesSyncEnabled: true,
                IsMetadataSyncEnabled: true,
                IsAutoSyncEnabled: false,
                IsFullHistoryEnabled: false,
                DelayMinSeconds: 0.5m,
                DelayMaxSeconds: 5.0m,
                StartSyncPeriodYears: 5
            );

            await Assert.ThrowsAsync<System.ArgumentOutOfRangeException>(() =>
                _pythonService.RunUpdatePipelineAsync("MSFT", invalidDelayConfig, null, default)
            );

            var invalidYearsConfig = new SyncSessionConfig(
                IsTimeSeriesSyncEnabled: true,
                IsMetadataSyncEnabled: true,
                IsAutoSyncEnabled: false,
                IsFullHistoryEnabled: false,
                DelayMinSeconds: 1.0m,
                DelayMaxSeconds: 5.0m,
                StartSyncPeriodYears: 100
            );

            await Assert.ThrowsAsync<System.ArgumentOutOfRangeException>(() =>
                _pythonService.RunUpdatePipelineAsync("MSFT", invalidYearsConfig, null, default)
            );
        }
    }
}
