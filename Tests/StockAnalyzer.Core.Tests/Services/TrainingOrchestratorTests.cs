using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
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
            var orchestrator = new TrainingOrchestrator(new FakePythonService(WriteFakeInterpreter(NeverCalledBatch)));

            await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.StartTrainingAsync(config));
        }

        [Fact]
        public async Task StartTrainingAsync_SuccessfulRun_StreamsProgressAndReturnsArtifactPaths()
        {
            var scriptPath = WriteFakeInterpreter(SuccessBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath));
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
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath));

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
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath));

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
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath));

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
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath));
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(300));

            var started = DateTime.UtcNow;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => orchestrator.StartTrainingAsync(ValidConfig(), ct: cts.Token));

            // The fake interpreter loops forever; a prompt return proves the process was killed
            // rather than the test waiting the loop out.
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
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
    }
}
