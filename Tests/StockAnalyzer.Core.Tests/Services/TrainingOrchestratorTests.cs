using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Portfolio;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;

namespace StockAnalyzer.Core.Tests.Services
{
    /// <summary>
    /// Exercises <see cref="TrainingOrchestrator"/>'s process orchestration (argument passing,
    /// stdout protocol parsing, cancellation, temp-config cleanup) against a fake "interpreter" --
    /// a tiny <c>.cmd</c> script standing in for <c>run_training.py</c> -- so these tests need
    /// neither the embedded Python.Included install nor any ML framework, and run everywhere
    /// <c>dotnet test</c> does. The real <c>run_training.py</c> protocol parsing rules themselves
    /// are covered by <see cref="TrainingProtocolLineTests"/> and were exercised end-to-end
    /// manually against real data during Task 3 (see the feature's step log).
    /// </summary>
    public class TrainingOrchestratorTests : IDisposable
    {
        // None of these tests configure PredictionFeatureMode.ComposedFeatures with an Indicator
        // channel, so IndicatorChannelExporter.ExportAsync always takes its empty-map fast path
        // and never touches NeverCalledMarketDataProvider below; one shared instance is enough.
        private static readonly IndicatorChannelExporter s_indicatorChannelExporter =
            new(new NeverCalledMarketDataProvider(), IndicatorFactory.Default);

        private readonly string _workDir;

        public TrainingOrchestratorTests()
        {
            _workDir = Path.Combine(Path.GetTempPath(), "sa_orch_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_workDir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        [Fact]
        public async Task StartTrainingAsync_InvalidConfig_ThrowsBeforeLaunchingAnyProcess()
        {
            var config = new TrainingJobConfig
            {
                Symbols = Array.Empty<string>(), // invalid: TrainingJobConfig.Validate() requires non-empty
                Architecture = "gbdt",
                WindowSize = 10,
                Horizon = 5,
            };
            var orchestrator = new TrainingOrchestrator(new FakePythonService(WriteFakeInterpreter(NeverCalledBatch)), s_indicatorChannelExporter);

            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.StartTrainingAsync(config));
        }

        [Fact]
        public async Task StartTrainingAsync_SuccessfulRun_StreamsProgressAndReturnsArtifactPaths()
        {
            var scriptPath = WriteFakeInterpreter(SuccessBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), s_indicatorChannelExporter);
            var progress = new SyncProgress<TrainingProgress>();

            var result = await orchestrator.StartTrainingAsync(ValidConfig(), progress);

            Assert.True(result.Success);
            Assert.Equal(0, result.ExitCode);
            Assert.NotNull(result.OnnxArtifactPath);
            Assert.True(File.Exists(result.OnnxArtifactPath));
            Assert.NotNull(result.MetricsArtifactPath);
            Assert.True(File.Exists(result.MetricsArtifactPath));
            Assert.Equal(0.65, result.Metrics["accuracy"]);
            Assert.Null(result.Message);

            // STAGE/PROGRESS/METRIC lines were forwarded as they streamed, ending at 100%.
            Assert.Contains(progress.Updates, u => u.Stage == "load" && u.Percent == 0);
            Assert.Contains(progress.Updates, u => u.Stage == "train" && u.Percent == 50);
            Assert.Contains(progress.Updates, u => u.Metric != null && u.Metric["accuracy"] == 0.65);
            Assert.Equal(100, progress.Updates[^1].Percent);
            Assert.Equal("done", progress.Updates[^1].Stage);
        }

        [Fact]
        public async Task StartTrainingAsync_TrainerExitsNonZero_ReturnsFailureResultWithStdErrMessage()
        {
            var scriptPath = WriteFakeInterpreter(FailureBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), s_indicatorChannelExporter);

            var result = await orchestrator.StartTrainingAsync(ValidConfig());

            Assert.False(result.Success);
            Assert.Equal(3, result.ExitCode);
            Assert.Null(result.OnnxArtifactPath);
            Assert.NotNull(result.Message);
            Assert.Contains("simulated failure", result.Message);
        }

        [Fact]
        public async Task StartTrainingAsync_ConfigTempFile_IsDeletedAfterTheRun()
        {
            var scriptPath = WriteFakeInterpreter(CaptureConfigPathBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), s_indicatorChannelExporter);

            await orchestrator.StartTrainingAsync(ValidConfig());

            // The batch script echoes the --config path it received to a marker file so the test
            // can find it without parsing process arguments directly.
            var markerPath = Path.Combine(_workDir, "captured_config_path.txt");
            Assert.True(File.Exists(markerPath));
            // %4 on the batch command line may carry surrounding quotes (ArgumentList quotes any
            // token containing special characters, e.g. a Temp path with a space); strip them so
            // the path below actually matches what the orchestrator wrote and deleted.
            var tempConfigPath = (await File.ReadAllTextAsync(markerPath)).Trim().Trim('"');
            Assert.False(string.IsNullOrEmpty(tempConfigPath));
            Assert.EndsWith(".json", tempConfigPath, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(tempConfigPath), $"Orchestrator should delete its temp config file: {tempConfigPath}");
        }

        [Fact]
        public async Task StartTrainingAsync_ConfigTempFile_HasNoUtf8Bom()
        {
            // Regression for: json.decoder.JSONDecodeError killing every run. run_training.py
            // reads the config with Path.read_text(encoding="utf-8"), which does not strip a
            // byte-order-mark, so a config file written with a BOM (as File.WriteAllTextAsync
            // does when passed the static Encoding.UTF8) fails json.loads before the trainer
            // does anything. The batch script below copies the raw --config file bytes to a
            // marker file before the orchestrator's `finally` deletes it.
            var scriptPath = WriteFakeInterpreter(CaptureConfigBytesBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), s_indicatorChannelExporter);

            await orchestrator.StartTrainingAsync(ValidConfig());

            var markerPath = Path.Combine(_workDir, "captured_config_bytes.bin");
            Assert.True(File.Exists(markerPath));
            var bytes = await File.ReadAllBytesAsync(markerPath);
            var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };
            Assert.False(
                bytes.Length >= 3 && bytes[0] == utf8Bom[0] && bytes[1] == utf8Bom[1] && bytes[2] == utf8Bom[2],
                "TrainingOrchestrator wrote the temp config file with a UTF-8 BOM, which breaks run_training.py's json.loads().");
            Assert.Equal('{', (char)bytes[0]);
        }

        [Fact]
        public async Task StartTrainingAsync_Cancelled_ThrowsOperationCanceledExceptionPromptly()
        {
            var scriptPath = WriteFakeInterpreter(LoopingBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), s_indicatorChannelExporter);
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(300));

            var started = DateTime.UtcNow;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => orchestrator.StartTrainingAsync(ValidConfig(), ct: cts.Token));

            // The fake interpreter loops forever; a prompt return proves the process was killed
            // rather than the test waiting the loop out.
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        }

        [Fact]
        public async Task StartTrainingAsync_ComposedFeaturesWithIndicatorChannel_WritesExportedPathsIntoConfigJson()
        {
            const string symbol = "TESTSYM";
            var candles = BuildIndicatorFixtureCandles();
            var exporter = new IndicatorChannelExporter(
                new FakeMarketDataProvider(new Dictionary<string, IReadOnlyList<CandleData>> { [symbol] = candles }),
                IndicatorFactory.Default);
            var scriptPath = WriteFakeInterpreter(CaptureConfigBytesBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), exporter);
            var config = ValidConfig() with
            {
                Symbols = new[] { symbol },
                FeatureMode = PredictionFeatureMode.ComposedFeatures,
                FeatureSpec = new FeatureSpec
                {
                    Channels = new[] { new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA } },
                },
            };

            string? exportedPath = null;
            try
            {
                await orchestrator.StartTrainingAsync(config);

                var markerPath = Path.Combine(_workDir, "captured_config_bytes.bin");
                Assert.True(File.Exists(markerPath));
                var json = await File.ReadAllTextAsync(markerPath);

                Assert.Contains("\"indicator_channel_export_paths\"", json);
                Assert.Contains($"\"{symbol}\"", json);

                using var doc = System.Text.Json.JsonDocument.Parse(json);
                exportedPath = doc.RootElement.GetProperty("indicator_channel_export_paths").GetProperty(symbol).GetString();
                Assert.False(string.IsNullOrEmpty(exportedPath));
                Assert.True(File.Exists(exportedPath), $"IndicatorChannelExporter's parquet file should exist at {exportedPath}");
            }
            finally
            {
                if (exportedPath is not null) { try { File.Delete(exportedPath); } catch { /* best-effort cleanup */ } }
            }
        }

        [Fact]
        public async Task StartTrainingAsync_ComposedFeaturesWithIndicatorChannelAndNonDailyTimeframe_ThrowsBeforeLaunchingAnyProcess()
        {
            var orchestrator = new TrainingOrchestrator(new FakePythonService(WriteFakeInterpreter(NeverCalledBatch)), s_indicatorChannelExporter);
            var config = ValidConfig() with
            {
                Timeframe = TrainingTimeframe.Weekly,
                FeatureMode = PredictionFeatureMode.ComposedFeatures,
                FeatureSpec = new FeatureSpec
                {
                    Channels = new[] { new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA } },
                },
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.StartTrainingAsync(config));
        }

        // Close = 100+i keeps CoreSmaIndicator's default Period=20 result well-defined without
        // depending on this test knowing the indicator's own formula.
        private static List<CandleData> BuildIndicatorFixtureCandles()
        {
            var candles = new List<CandleData>();
            for (int i = 0; i < 25; i++)
            {
                candles.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 105m, 95m, 100m + i, 1000));
            }
            return candles;
        }

        // Returns real fixture candles for a configured symbol (unlike NeverCalledMarketDataProvider
        // below, whose every member throws); used only by tests that need ExportAsync to actually
        // compute and write a parquet file.
        private sealed class FakeMarketDataProvider : IMarketDataProvider
        {
            private readonly IReadOnlyDictionary<string, IReadOnlyList<CandleData>> _bySymbol;
            public FakeMarketDataProvider(IReadOnlyDictionary<string, IReadOnlyList<CandleData>> bySymbol) => _bySymbol = bySymbol;

            public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame)
                => _bySymbol.TryGetValue(symbol, out var candles)
                    ? Task.FromResult(candles)
                    : throw new KeyNotFoundException($"FakeMarketDataProvider: no fixture candles configured for '{symbol}'.");

            public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => throw new NotSupportedException();
            public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => throw new NotSupportedException();
            public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => throw new NotSupportedException();
            public ValueTask<TickerMetadata> GetMetadataAsync(string ticker) => throw new NotSupportedException();
            public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => throw new NotSupportedException();
            public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => throw new NotSupportedException();
            public Task AddTickerAsync(string symbol) => throw new NotSupportedException();
            public Task AddTickersAsync(IEnumerable<string> symbols) => throw new NotSupportedException();
            public Task RemoveTickerAsync(string symbol) => throw new NotSupportedException();
            public Task RemoveTickersAsync(IEnumerable<string> symbols) => throw new NotSupportedException();
            public void InvalidateMetadataCache(string ticker) => throw new NotSupportedException();
            public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => throw new NotSupportedException();
            public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => throw new NotSupportedException();
        }

        // --- fixtures ------------------------------------------------------

        private static TrainingJobConfig ValidConfig() => new()
        {
            Symbols = new[] { "TESTSYM" },
            Architecture = "gbdt",
            WindowSize = 10,
            Horizon = 5,
            Framework = TrainingFramework.LightGBM,
        };

        private const string NeverCalledBatch = "@echo off\r\nexit /b 99\r\n";

        private const string SuccessBatch =
            "@echo off\r\n" +
            "echo STAGE:load\r\n" +
            "echo PROGRESS:0\r\n" +
            "echo STAGE:train\r\n" +
            "echo PROGRESS:50\r\n" +
            "echo METRIC:{\"accuracy\":0.65,\"n_samples\":100.0}\r\n" +
            "echo STAGE:export\r\n" +
            "echo PROGRESS:90\r\n" +
            "echo fake-onnx> \"%~dp0fake_model.onnx\"\r\n" +
            "echo {}> \"%~dp0fake_model.onnx.metrics.json\"\r\n" +
            "echo ARTIFACT:onnx:%~dp0fake_model.onnx\r\n" +
            "echo ARTIFACT:metrics:%~dp0fake_model.onnx.metrics.json\r\n" +
            "echo STAGE:done\r\n" +
            "echo PROGRESS:100\r\n" +
            "exit /b 0\r\n";

        private const string FailureBatch =
            "@echo off\r\n" +
            "echo STAGE:load\r\n" +
            "echo STDERR: simulated failure 1>&2\r\n" +
            "exit /b 3\r\n";

        private const string LoopingBatch =
            "@echo off\r\n" +
            "echo STAGE:load\r\n" +
            ":loop\r\n" +
            "ping -n 2 127.0.0.1 >nul\r\n" +
            "goto loop\r\n";

        // %4 is the value following "--config" in the fixed "-u <script> --config <path>" argv
        // TrainingOrchestrator builds.
        private const string CaptureConfigPathBatch =
            "@echo off\r\n" +
            "echo %4> \"%~dp0captured_config_path.txt\"\r\n" +
            "echo STAGE:done\r\n" +
            "echo PROGRESS:100\r\n" +
            "exit /b 0\r\n";

        // Copies the --config file's raw bytes (not just its path) to a marker file before the
        // orchestrator's `finally` deletes it, so the test can inspect the leading bytes for a BOM.
        private const string CaptureConfigBytesBatch =
            "@echo off\r\n" +
            "copy %4 \"%~dp0captured_config_bytes.bin\" >nul\r\n" +
            "echo STAGE:done\r\n" +
            "echo PROGRESS:100\r\n" +
            "exit /b 0\r\n";

        private string WriteFakeInterpreter(string batchBody)
        {
            var path = Path.Combine(_workDir, "fake_python_" + Guid.NewGuid().ToString("N") + ".cmd");
            File.WriteAllText(path, batchBody);
            return path;
        }

        // Re-lists IPythonService (not just inherited from MockPythonServiceBase) so this class's
        // ResolvePythonExecutablePathAsync is re-bound as the interface implementation instead of
        // silently falling back to IPythonService's own default (NotSupportedException) body --
        // C# only reconsiders a default interface method at the class that (re)declares the
        // interface, not at an arbitrary derived class that merely adds a same-named method.
        private sealed class FakePythonService : MockPythonServiceBase, IPythonService
        {
            private readonly string _executablePath;

            public FakePythonService(string executablePath) => _executablePath = executablePath;

            public Task<string> ResolvePythonExecutablePathAsync(CancellationToken ct = default)
                => Task.FromResult(_executablePath);
        }

        private sealed class SyncProgress<T> : IProgress<T>
        {
            public List<T> Updates { get; } = new();
            public void Report(T value) => Updates.Add(value);
        }

        // IndicatorChannelExporter dependency for TrainingOrchestrator's constructor. Every member
        // throws because no test in this class configures an Indicator-kind ComposedFeatures
        // channel, so ExportAsync always returns via its empty-map fast path without ever calling
        // into this provider.
        private sealed class NeverCalledMarketDataProvider : IMarketDataProvider
        {
            private static NotSupportedException NotExpected([System.Runtime.CompilerServices.CallerMemberName] string member = "")
                => new($"{member} should not be called: no test configures an Indicator-kind ComposedFeatures channel.");

            public Task<IReadOnlyList<string>> GetAvailableTickersAsync() => throw NotExpected();
            public Task<IReadOnlyList<CandleData>> GetTickersDataAsync(string symbol, TimeFrame timeFrame) => throw NotExpected();
            public Task<IReadOnlyList<string>> ScreenAsync(ScreeningCriteria criteria) => throw NotExpected();
            public Task<IReadOnlyDictionary<string, decimal>> GetLatestPricesAsync(IEnumerable<string> symbols) => throw NotExpected();
            public ValueTask<TickerMetadata> GetMetadataAsync(string ticker) => throw NotExpected();
            public Task<TickerMetadata> FetchMetadataFromPythonAsync(string ticker) => throw NotExpected();
            public Task SaveMetadataAsync(string ticker, TickerMetadata meta) => throw NotExpected();
            public Task AddTickerAsync(string symbol) => throw NotExpected();
            public Task AddTickersAsync(IEnumerable<string> symbols) => throw NotExpected();
            public Task RemoveTickerAsync(string symbol) => throw NotExpected();
            public Task RemoveTickersAsync(IEnumerable<string> symbols) => throw NotExpected();
            public void InvalidateMetadataCache(string ticker) => throw NotExpected();
            public Task<DateTimeOffset?> GetTimeSeriesLastUpdatedAsync(string symbol) => throw NotExpected();
            public Task<int> DeleteTickerDataFromDateAsync(string symbol, DateTime cutoffDate) => throw NotExpected();
        }
    }
}
