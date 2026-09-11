using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Indicators.Advanced;
using StockAnalyzer.Core.Services;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Tests
{
    /// <summary>
    /// Regression test for a bug where PythonService.InitializeExternalProcessAsync only ever
    /// called PythonProcessManager.StartAsync() once (gated on _processManager == null). Once
    /// any transient failure tore the named-pipe connection down (PythonProcessManager's
    /// catch-all calls CleanupConnection(), nulling the pipe reader/writer/client), the
    /// long-lived PythonService singleton stayed permanently poisoned for the rest of the app
    /// session: every subsequent Python-backed indicator dereferenced the null pipe fields via
    /// the `!` null-forgiving operators in PythonProcessManager.SendCommandAsync, surfacing as
    /// a bare "Object reference not set to an instance of an object." on whichever indicator
    /// happened to be calculated next -- reported by a user as "FFT Cycle: Object reference not
    /// set to an instance of an object", though the indicator itself was not at fault.
    /// </summary>
    [Collection("PythonIpc")]
    public class PythonServiceReconnectionTests
    {
        [Fact]
        public async Task CalculateAsync_SelfHealsAfterPriorConnectionFailure()
        {
            var settings = new RealPythonTestSettings("testreconnectpipe1");
            var pythonService = new PythonService(settings);
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            var start = new System.DateTime(2020, 1, 1);
            var normalCandles = new List<CoreCandleData>();
            for (int i = 0; i < 100; i++)
            {
                decimal close = 100m + (decimal)System.Math.Sin(2 * System.Math.PI * i / 20) * 10m;
                normalCandles.Add(new CoreCandleData(start.AddDays(i), close, close + 1m, close - 1m, close, 1000));
            }

            var indicator = new CoreFFTCycleIndicator();
            var context = new CoreExecutionContext(pythonService);

            // 0) Establish a healthy baseline connection.
            var baselineResult = await indicator.CalculateAsync(normalCandles, context);
            Assert.True(baselineResult.IsSuccessful, $"Precondition: baseline calculation must succeed: {baselineResult.ErrorMessage}");

            // 1) Directly simulate what any transient IOException/etc. does in production:
            //    PythonProcessManager.CleanupConnection() nulls the pipe reader/writer/client
            //    and sets _isConnected=false. Invoked via reflection so this test does not
            //    depend on any particular failure trigger (an indirect trigger, such as an
            //    oversized payload, is fragile -- fixing that bug elsewhere silently
            //    invalidates the precondition, as happened here when the ERROR_MORE_DATA
            //    chunked-read bug was fixed and large datasets stopped failing).
            var processManagerField = typeof(PythonService).GetField("_processManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var processManager = processManagerField!.GetValue(pythonService);
            Assert.NotNull(processManager);
            var cleanupMethod = processManager!.GetType().GetMethod("CleanupConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            cleanupMethod!.Invoke(processManager, null);

            // 2) A normal, small dataset on the SAME PythonService instance afterwards must
            //    succeed -- proving the connection self-heals instead of staying permanently
            //    broken for the rest of the app session.
            var healedResult = await indicator.CalculateAsync(normalCandles, context);
            Assert.True(healedResult.IsSuccessful, $"Expected the connection to self-heal, but got: {healedResult.ErrorMessage}");
        }

        private class RealPythonTestSettings : IStockAnalyzerSettings
        {
            private readonly string _pipeName;
            public RealPythonTestSettings(string pipeName) => _pipeName = pipeName;

            public string? PythonPath => null;
            public string PythonScriptDirectory => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.AppContext.BaseDirectory, "..", "..", "..", "..", "..", "StockAnalyzer.Core", "Scripts"));
            public string PythonServerScriptName => "server.py";
            public int PythonMaxRetries => 3;
            public int PythonBackoffMs => 100;
            public int PythonHealthCheckIntervalMs => 1000;
            public int PipeConnectPollIntervalMs => 100;
            public int SyncTimeoutMinutes => 1;
            public IReadOnlyList<string> PythonEssentialPackages => new[] { "pandas", "numpy", "scipy" };
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
            public string PipeName => _pipeName;
            public int PipeConnectionTimeoutMs => 5000;
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
    }
}
