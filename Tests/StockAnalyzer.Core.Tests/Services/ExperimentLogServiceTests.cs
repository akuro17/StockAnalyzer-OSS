using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services
{
    /// <summary>
    /// <see cref="ExperimentLogService"/> resolves its target through the real, non-mockable
    /// <see cref="PathDiscovery.ResolveExperimentsDirectory"/>, so these tests write under the
    /// real <c>&lt;DataRoot&gt;/Experiments/</c> directory using a unique per-test run id and
    /// always delete the created run directory (see <see cref="Dispose"/>).
    /// </summary>
    public class ExperimentLogServiceTests : IDisposable
    {
        private readonly List<string> _runDirsToClean = new();

        public void Dispose()
        {
            foreach (var dir in _runDirsToClean)
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
                catch { /* best-effort cleanup */ }
            }
        }

        [Fact]
        public async Task RecordAsync_ValidRun_WritesConfigAndMetricsJsonUnderRunDirectory()
        {
            var config = ValidConfig();
            var result = ValidResult(runId: NewRunId());
            var service = new ExperimentLogService();

            await service.RecordAsync(config, result);
            var runDir = TrackAndResolve(result.RunId);

            Assert.True(File.Exists(Path.Combine(runDir, "config.json")));
            Assert.True(File.Exists(Path.Combine(runDir, "metrics.json")));
        }

        [Fact]
        public async Task RecordAsync_ConfigJson_RoundTripsToTheOriginalConfig()
        {
            var config = ValidConfig();
            var result = ValidResult(runId: NewRunId());
            var service = new ExperimentLogService();

            await service.RecordAsync(config, result);
            var runDir = TrackAndResolve(result.RunId);

            var roundTripped = TrainingConfigJson.DeserializeConfig(
                await File.ReadAllTextAsync(Path.Combine(runDir, "config.json")));

            Assert.Equal(config.Symbols, roundTripped.Symbols);
            Assert.Equal(config.Architecture, roundTripped.Architecture);
            Assert.Equal(config.WindowSize, roundTripped.WindowSize);
            Assert.Equal(config.Horizon, roundTripped.Horizon);
            Assert.Equal(config.Framework, roundTripped.Framework);
            Assert.Equal(config.Timeframe, roundTripped.Timeframe);
        }

        [Fact]
        public async Task RecordAsync_MetricsJson_ContainsArtifactPathsAndMetrics()
        {
            var config = ValidConfig();
            var result = ValidResult(runId: NewRunId());
            var service = new ExperimentLogService();

            await service.RecordAsync(config, result);
            var runDir = TrackAndResolve(result.RunId);

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "metrics.json")));
            var root = doc.RootElement;

            Assert.Equal(result.RunId, root.GetProperty("run_id").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(result.OnnxArtifactPath, root.GetProperty("onnx_artifact_path").GetString());
            Assert.Equal(0.5, root.GetProperty("metrics").GetProperty("accuracy").GetDouble());
        }

        [Fact]
        public async Task RecordAsync_ConfigAndMetricsJson_HaveNoUtf8BomAndNoLeftoverTempFiles()
        {
            // Regression for: RecordAsync originally wrote config.json/metrics.json via
            // File.WriteAllTextAsync (a direct, non-atomic overwrite) - see
            // Y:\Temp\sa_constraint_report_OnnxTrainingFoundation.md Finding #2. Switching to
            // AtomicJsonFile.SaveAsync must keep both files BOM-free (JsonSerializer.SerializeAsync
            // never emits one) and must not leave its own ".tmp" swap file behind.
            var config = ValidConfig();
            var result = ValidResult(runId: NewRunId());
            var service = new ExperimentLogService();

            await service.RecordAsync(config, result);
            var runDir = TrackAndResolve(result.RunId);

            var configPath = Path.Combine(runDir, "config.json");
            var metricsPath = Path.Combine(runDir, "metrics.json");

            foreach (var path in new[] { configPath, metricsPath })
            {
                var bytes = await File.ReadAllBytesAsync(path);
                Assert.False(
                    bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                    $"{path} was written with a UTF-8 BOM.");
                Assert.False(File.Exists(path + ".tmp"), $"Leftover atomic-write temp file: {path}.tmp");
            }
        }

        [Fact]
        public async Task RecordAsync_NullConfig_ThrowsArgumentNullException()
        {
            var service = new ExperimentLogService();
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => service.RecordAsync(null!, ValidResult(NewRunId())));
        }

        [Fact]
        public async Task RecordAsync_NullResult_ThrowsArgumentNullException()
        {
            var service = new ExperimentLogService();
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => service.RecordAsync(ValidConfig(), null!));
        }

        private string TrackAndResolve(string runId)
        {
            var dir = PathDiscovery.ResolveExperimentsDirectory(runId);
            _runDirsToClean.Add(dir);
            return dir;
        }

        private static string NewRunId() => "sa_test_" + Guid.NewGuid().ToString("N");

        private static TrainingJobConfig ValidConfig() => new()
        {
            Symbols = new[] { "TESTSYM" },
            Architecture = "gbdt",
            WindowSize = 10,
            Horizon = 5,
            Framework = TrainingFramework.LightGBM,
        };

        private static TrainingRunResult ValidResult(string runId) => new()
        {
            RunId = runId,
            Success = true,
            ExitCode = 0,
            OnnxArtifactPath = @"I:\stock\StockAnalyzer.Python\training\artifacts\test.onnx",
            MetricsArtifactPath = @"I:\stock\StockAnalyzer.Python\training\artifacts\test.onnx.metrics.json",
            Metrics = new Dictionary<string, double> { ["accuracy"] = 0.5 },
            StartedUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedUtc = DateTimeOffset.UtcNow,
        };
    }
}
