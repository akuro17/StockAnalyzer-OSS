using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Moq;
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
    /// neither the embedded Python install nor any ML framework, and run everywhere
    /// <c>dotnet test</c> does. The real <c>run_training.py</c> protocol parsing rules themselves
    /// are covered by <see cref="TrainingProtocolLineTests"/> and were exercised end-to-end
    /// manually against real data during Task 3 (see the feature's step log).
    /// </summary>
    /// <remarks>
    /// Runs in the <c>ChildProcess</c> collection (alone): the orchestrator starts the fake interpreter as a real child process, which inherits the
    /// open ONNX model handles of any test running beside it and keeps their temp folders locked while it lives (R2-5). The guard test cannot see
    /// this because the process is started by production code, not by a starter this file calls.
    /// </remarks>
    [Collection("ChildProcess")]
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
        public async Task Resume_PreservesManifestRunId()
        {
            var manifest = Path.Combine(_workDir, "run.json");
            File.WriteAllText(manifest, "{\"schema_version\":1,\"kind\":\"training_run\",\"run_id\":\"original_run\"}");
            var config = new TrainingJobConfig
            {
                Symbols = new[] { "X" }, Architecture = "lstm", WindowSize = 4, Horizon = 1,
                InitializationMode = TrainingInitializationMode.Resume, CheckpointPath = manifest,
            };
            var orchestrator = new TrainingOrchestrator(new FakePythonService(WriteFakeInterpreter(SuccessBatch)),
                s_indicatorChannelExporter);
            var result = await orchestrator.StartTrainingAsync(config);
            Assert.True(result.Success);
            Assert.Equal("original_run", result.RunId);
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
            Assert.Equal(Path.Combine(_workDir, "run.json"), result.CheckpointManifestPath);
            Assert.Contains(progress.Updates, u => u.CheckpointManifestPath == result.CheckpointManifestPath);
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
        public async Task StartTrainingAsync_EnsembleRun_ReturnsTheManifestBesideTheFirstMemberArtifact()
        {
            var scriptPath = WriteFakeInterpreter(SuccessBatch.Replace(
                "echo ARTIFACT:checkpoint",
                "echo ARTIFACT:ensemble_member:%~dp0fake_model.onnx\r\n" +
                "echo ARTIFACT:ensemble:%~dp0group.ensemble.json\r\n" +
                "echo ARTIFACT:checkpoint"));
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), s_indicatorChannelExporter);

            var result = await orchestrator.StartTrainingAsync(ValidConfig() with
            {
                EnsembleMethod = TrainingEnsembleMethod.Bagging, EnsembleMembers = 3,
            });

            Assert.True(result.Success);
            Assert.Equal(Path.Combine(_workDir, "group.ensemble.json"), result.EnsembleManifestPath);
            Assert.NotNull(result.OnnxArtifactPath); // the first member keeps single-model consumers working
            var single = await new TrainingOrchestrator(new FakePythonService(WriteFakeInterpreter(SuccessBatch)),
                s_indicatorChannelExporter).StartTrainingAsync(ValidConfig());
            Assert.Null(single.EnsembleManifestPath);
        }

        // Two published members (with sidecars), their manifest and neighbours whose name only shares the member's prefix.
        private const string PublishTwoMembersBatch =
            "@echo off\r\n" +
            "echo STAGE:load\r\n" +
            // the run id the orchestrator wrote into the --config file (%4), to name checkpoint directories like the trainer does
            "for /f \"tokens=2 delims=:, \" %%a in ('findstr /c:run_id \"%~4\"') do set RID=%%~a\r\n" +
            "echo %RID%> \"%~dp0captured_run_id.txt\"\r\n" +
            "echo ARTIFACT:ensemble_checkpoints:%~dp0checkpoints\r\n" +
            "for %%d in (\"%RID%-m01\" \"%RID%-m02\" \"%RID%\" \"%RID%-mx\" \"%RID%x-m01\" \"other-run-m01\") do " +
            "(mkdir \"%~dp0checkpoints\\%%~d\\fold_0\" & echo c> \"%~dp0checkpoints\\%%~d\\fold_0\\last.json\")\r\n" +
            "echo m> \"%~dp0grp_m01.onnx\"\r\n" +
            "echo {}> \"%~dp0grp_m01.onnx.metrics.json\"\r\n" +
            "echo {}> \"%~dp0grp_m01.onnx.analysis.json\"\r\n" +
            "echo ARTIFACT:ensemble_member:%~dp0grp_m01.onnx\r\n" +
            "echo m> \"%~dp0grp_m02.onnx\"\r\n" +
            "echo {}> \"%~dp0grp_m02.onnx.metrics.json\"\r\n" +
            "echo ARTIFACT:ensemble_member:%~dp0grp_m02.onnx\r\n" +
            "echo {}> \"%~dp0grp.ensemble.json\"\r\n" +
            "echo ARTIFACT:ensemble:%~dp0grp.ensemble.json\r\n" +
            "echo keep> \"%~dp0grp_m010.onnx\"\r\n" +
            "echo keep> \"%~dp0grp_m01.onnx2\"\r\n";

        private string[] MemberFiles() => new[] { "grp_m01.onnx", "grp_m01.onnx.metrics.json", "grp_m01.onnx.analysis.json",
            "grp_m02.onnx", "grp_m02.onnx.metrics.json", "grp.ensemble.json" }.Select(name => Path.Combine(_workDir, name)).ToArray();

        private string[] NeighbourFiles() => new[] { "grp_m010.onnx", "grp_m01.onnx2" }
            .Select(name => Path.Combine(_workDir, name)).ToArray();

        private string RunId()
        {
            var id = File.ReadAllText(Path.Combine(_workDir, "captured_run_id.txt")).Trim();
            Assert.Matches(@"^\d{8}-\d{9}-[0-9a-f]{8}$", id); // the id the batch read from the config really is the run's
            return id;
        }

        /// <summary>The checkpoint directories of this run's members (`&lt;run id&gt;-mNN`): the only ones a rollback may remove.</summary>
        private string[] MemberCheckpointDirectories() => new[] { "-m01", "-m02" }
            .Select(suffix => Path.Combine(_workDir, "checkpoints", RunId() + suffix)).ToArray();

        /// <summary>Directories that merely look similar (the run's own plain directory, a non-numeric suffix, another run).</summary>
        private string[] OtherCheckpointDirectories() => new[] { RunId(), RunId() + "-mx", RunId() + "x-m01", "other-run-m01" }
            .Select(name => Path.Combine(_workDir, "checkpoints", name)).ToArray();

        [Fact]
        public async Task StartTrainingAsync_EnsembleFailsAfterPublishingMembers_RollsBackMembersSidecarsAndManifest()
        {
            var scriptPath = WriteFakeInterpreter(PublishTwoMembersBatch + "echo STDERR: member 3 failed 1>&2\r\nexit /b 3\r\n");
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), s_indicatorChannelExporter);

            var result = await orchestrator.StartTrainingAsync(ValidConfig() with
            {
                EnsembleMethod = TrainingEnsembleMethod.Bagging, EnsembleMembers = 3,
            });

            Assert.False(result.Success);
            Assert.Contains("member 3 failed", result.Message); // the run's own outcome is not replaced
            Assert.All(MemberFiles(), file => Assert.False(File.Exists(file), file));
            Assert.All(NeighbourFiles(), file => Assert.True(File.Exists(file), file)); // only `<onnx>` and `<onnx>.*`
            Assert.All(MemberCheckpointDirectories(), directory => Assert.False(Directory.Exists(directory), directory));
            Assert.All(OtherCheckpointDirectories(), directory => Assert.True(Directory.Exists(directory), directory));
        }

        [Fact]
        public async Task StartTrainingAsync_EnsembleFails_NeverDeletesAnnouncedPathsOutsideTheRunsOwnFiles()
        {
            // Lines the trainer must never be able to turn into deletes: a member file in another directory, a name that is not a
            // member name, a manifest that is not an ensemble manifest, and a checkpoint root that is not <directory>/checkpoints.
            var foreign = Path.Combine(_workDir, "foreign");
            Directory.CreateDirectory(Path.Combine(foreign, "checkpoints", "run-m01"));
            var outsiders = new[]
            {
                Path.Combine(foreign, "victim_m01.onnx"), Path.Combine(foreign, "victim_m01.onnx.metrics.json"),
                Path.Combine(_workDir, "plain.onnx"), Path.Combine(_workDir, "plain.onnx.metrics.json"),
                Path.Combine(_workDir, "notes.json"),
            };
            foreach (var file in outsiders) File.WriteAllText(file, "keep");
            var scriptPath = WriteFakeInterpreter(PublishTwoMembersBatch +
                $"echo ARTIFACT:ensemble_member:{foreign}\\victim_m01.onnx\r\n" +
                "echo ARTIFACT:ensemble_member:%~dp0plain.onnx\r\n" +
                "echo ARTIFACT:ensemble:%~dp0notes.json\r\n" +
                $"echo ARTIFACT:ensemble_checkpoints:{foreign}\\checkpoints\r\n" +
                "echo STDERR: member 3 failed 1>&2\r\nexit /b 3\r\n");
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), s_indicatorChannelExporter);

            var result = await orchestrator.StartTrainingAsync(ValidConfig() with
            {
                EnsembleMethod = TrainingEnsembleMethod.Bagging, EnsembleMembers = 3,
            });

            Assert.False(result.Success);
            Assert.All(outsiders, file => Assert.True(File.Exists(file), file));
            Assert.True(Directory.Exists(Path.Combine(foreign, "checkpoints", "run-m01")));
            Assert.All(MemberFiles().Where(f => !f.EndsWith("grp.ensemble.json")), file => Assert.False(File.Exists(file), file)); // the run's own members still go
        }

        [Fact]
        public async Task StartTrainingAsync_EnsembleCancelledAfterPublishingMembers_RollsBackMembersSidecarsAndManifest()
        {
            var scriptPath = WriteFakeInterpreter(PublishTwoMembersBatch +
                ":loop\r\nping -n 2 127.0.0.1 >nul\r\ngoto loop\r\n");
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), s_indicatorChannelExporter);
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => orchestrator.StartTrainingAsync(
                ValidConfig() with { EnsembleMethod = TrainingEnsembleMethod.Bagging, EnsembleMembers = 3 }, ct: cts.Token));

            Assert.All(MemberFiles(), file => Assert.False(File.Exists(file), file));
            Assert.All(NeighbourFiles(), file => Assert.True(File.Exists(file), file));
            Assert.All(MemberCheckpointDirectories(), directory => Assert.False(Directory.Exists(directory), directory));
            Assert.All(OtherCheckpointDirectories(), directory => Assert.True(Directory.Exists(directory), directory));
        }

        [Fact]
        public async Task StartTrainingAsync_EnsembleSucceeds_KeepsEveryMemberFileAndTheManifest()
        {
            var scriptPath = WriteFakeInterpreter(PublishTwoMembersBatch +
                "echo ARTIFACT:onnx:%~dp0grp_m01.onnx\r\n" +
                "exit /b 0\r\n");
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath), s_indicatorChannelExporter);

            var result = await orchestrator.StartTrainingAsync(ValidConfig() with
            {
                EnsembleMethod = TrainingEnsembleMethod.Bagging, EnsembleMembers = 3,
            });

            Assert.True(result.Success);
            Assert.All(MemberFiles(), file => Assert.True(File.Exists(file), file));
            Assert.All(MemberCheckpointDirectories(), directory => Assert.True(Directory.Exists(directory), directory)); // success keeps them
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
        public async Task StartTrainingAsync_ChildUsesStartSnapshotWithoutChangingParentEnvironment()
        {
            var settingsPath = Path.Combine(_workDir, TrainingResourceSettings.FileName);
            var settings = new TrainingResourceSettings(settingsPath);
            var owner = new object();
            Assert.True(settings.TryAcquirePreview(owner));
            settings.SetPreview(owner, new TrainingResourceOverrides(7, null, 23));
            var original = Environment.GetEnvironmentVariable("SA_MAX_COMPOSED_CHANNELS");
            var script = WriteFakeInterpreter(CaptureResourceEnvironmentBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(script),
                s_indicatorChannelExporter, resourceSettings: settings);

            var running = orchestrator.StartTrainingAsync(ValidConfig());
            settings.SetPreview(owner, new TrainingResourceOverrides(8, null, 24));
            await running;

            var line = (await File.ReadAllTextAsync(Path.Combine(_workDir, "captured_limits.txt"))).Trim();
            Assert.StartsWith("7,", line, StringComparison.Ordinal);
            Assert.Contains(",23,", line, StringComparison.Ordinal);
            using var captured = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(_workDir, "captured_resource_config.json")));
            var limits = captured.RootElement.GetProperty("resource_limits");
            Assert.Equal(7, limits.GetProperty("max_channels").GetInt32());
            Assert.Equal(23, limits.GetProperty("max_samples").GetInt32());
            Assert.Equal(TrainingResourceOverrides.MaximumEvaluationFolds,
                limits.GetProperty("max_evaluation_folds").GetInt32());
            Assert.Equal(TrainingResourceOverrides.MaximumRunDurationMinutes * 60,
                limits.GetProperty("max_run_duration_seconds").GetInt32());
            Assert.InRange(captured.RootElement.GetProperty("remaining_run_budget_seconds").GetDouble(),
                0.001, limits.GetProperty("max_run_duration_seconds").GetDouble());
            Assert.Equal(limits.GetProperty("max_channels").GetInt32(),
                (await running).ResourceLimits!.MaxChannels);
            Assert.Equal(original, Environment.GetEnvironmentVariable("SA_MAX_COMPOSED_CHANNELS"));
            settings.ReleasePreview(owner);
        }

        [Fact]
        public async Task StartTrainingAsync_EphemeralRun_ChildGetsNoBytecodeCacheTheArtifactsDirectoryAndTheCallersRunId()
        {
            var runId = TrainingRunId.New(DateTimeOffset.UtcNow);
            var artifacts = Path.Combine(StockAnalyzer.Core.Execution.EphemeralSandbox.Root, "session", runId);
            var script = WriteFakeInterpreter(CaptureBytecodeEnvironmentBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(script), s_indicatorChannelExporter);

            var result = await orchestrator.StartTrainingAsync(ValidConfig() with { ArtifactsDirectory = artifacts, RunId = runId });

            Assert.Equal(runId, result.RunId);
            Assert.Equal("[1]", (await File.ReadAllTextAsync(Path.Combine(_workDir, "captured_bytecode.txt"))).Trim());
            using var captured = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_workDir, "captured_bytecode_config.json")));
            Assert.Equal(artifacts, captured.RootElement.GetProperty("artifacts_directory").GetString());
            Assert.Equal(runId, captured.RootElement.GetProperty("run_id").GetString());
        }

        [Fact]
        public async Task StartTrainingAsync_NormalRun_ChildEnvironmentAndWireAreUnchangedAndTheOrchestratorMakesTheRunId()
        {
            var script = WriteFakeInterpreter(CaptureBytecodeEnvironmentBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(script), s_indicatorChannelExporter);
            var callersId = TrainingRunId.New(DateTimeOffset.UtcNow);

            var result = await orchestrator.StartTrainingAsync(ValidConfig() with { RunId = callersId });

            // The caller's id is ignored outside the ephemeral mode: the orchestrator makes its own, in the same format as before.
            Assert.True(TrainingRunId.IsValid(result.RunId));
            Assert.NotEqual(callersId, result.RunId);
            // The batch echoes the value in brackets (an unset variable is empty); a run of the normal mode must see exactly what the parent has.
            var inherited = Environment.GetEnvironmentVariable("PYTHONDONTWRITEBYTECODE") ?? string.Empty;
            Assert.Equal($"[{inherited}]", (await File.ReadAllTextAsync(Path.Combine(_workDir, "captured_bytecode.txt"))).Trim());
            using var captured = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_workDir, "captured_bytecode_config.json")));
            Assert.False(captured.RootElement.TryGetProperty("artifacts_directory", out _));
            Assert.Equal(result.RunId, captured.RootElement.GetProperty("run_id").GetString());
        }

        [Fact]
        public async Task StartTrainingAsync_WithAResolver_TheInterpreterIsTheResolversAndAMissingOneIsAFileNotFound()
        {
            var script = WriteFakeInterpreter(SuccessBatch);
            var found = new TrainingOrchestrator(new FakePythonService(WriteFakeInterpreter(NeverCalledBatch)), s_indicatorChannelExporter,
                executableResolver: new FixedResolver(script));
            var missing = new TrainingOrchestrator(new FakePythonService(script), s_indicatorChannelExporter,
                executableResolver: new FixedResolver(null));

            Assert.True((await found.StartTrainingAsync(ValidConfig())).Success);
            await Assert.ThrowsAsync<FileNotFoundException>(() => missing.StartTrainingAsync(ValidConfig()));
        }

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
        public async Task StartTrainingAsync_DeadlineDuringChildExecution_KillsChildAndThrowsTimeout()
        {
            var scriptPath = WriteFakeInterpreter(LoopingBatch);
            var orchestrator = new TrainingOrchestrator(new FakePythonService(scriptPath),
                s_indicatorChannelExporter, maxRunDurationSeconds: 1);

            var started = DateTime.UtcNow;
            await Assert.ThrowsAsync<TimeoutException>(() => orchestrator.StartTrainingAsync(ValidConfig()));

            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(Path.Combine(_workDir, "fake_model.onnx")));
        }

        [Fact]
        public async Task StartTrainingAsync_DeadlineDuringSourcePreparation_FailsBeforeLaunchingChild()
        {
            var config = ValidConfig() with
            {
                Timeframe = TrainingTimeframe.Weekly,
                FeatureMode = PredictionFeatureMode.ComposedFeatures,
                FeatureSpec = new FeatureSpec { Channels = new[]
                {
                    new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA },
                } },
                SourceManifestPath = Path.Combine(_workDir, "manifest.json"),
            };
            string? runDirectory = null;
            var never = new TaskCompletionSource<PreparedTrainingInput>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new Mock<ITrainingSourceSnapshotProvider>();
            provider.Setup(p => p.PrepareAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<string>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
                .Returns((TrainingJobConfig job, string path, DateTimeOffset started, IProgress<string> stage,
                    CancellationToken token) =>
                {
                    runDirectory = path;
                    return never.Task.WaitAsync(token);
                });
            var orchestrator = new TrainingOrchestrator(
                new FakePythonService(WriteFakeInterpreter(NeverCalledBatch)),
                s_indicatorChannelExporter, maxRunDurationSeconds: 1, sourceSnapshotProvider: provider.Object);
            var progress = new SyncProgress<TrainingProgress>();

            await Assert.ThrowsAsync<TimeoutException>(() => orchestrator.StartTrainingAsync(config, progress));

            Assert.NotNull(runDirectory);
            Assert.False(Directory.Exists(runDirectory));
            Assert.Equal("Failed", progress.Updates[^1].Stage);
            Assert.False(File.Exists(Path.Combine(_workDir, "fake_model.onnx")));
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

        [Fact]
        public async Task StartTrainingAsync_PreparedCancellation_DeletesOnlyItsRunDirectory()
        {
            var config = ValidConfig() with
            {
                Timeframe = TrainingTimeframe.Weekly,
                FeatureMode = PredictionFeatureMode.ComposedFeatures,
                FeatureSpec = new FeatureSpec { Channels = new[]
                {
                    new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA },
                } },
                SourceManifestPath = Path.Combine(_workDir, "manifest.json"),
            };
            string? runDirectory = null;
            using var cancel = new CancellationTokenSource();
            var provider = new Mock<ITrainingSourceSnapshotProvider>();
            provider.Setup(p => p.PrepareAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
                    It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
                .Returns((TrainingJobConfig job, string path, DateTimeOffset start,
                    IProgress<string> stage, CancellationToken token) =>
                {
                    runDirectory = path;
                    Directory.CreateDirectory(path);
                    File.WriteAllText(Path.Combine(path, "marker"), "owned by this run");
                    stage.Report("Snapshotting");
                    cancel.Cancel();
                    stage.Report("LateSnapshotting");
                    return Task.FromCanceled<PreparedTrainingInput>(cancel.Token);
                });
            var orchestrator = new TrainingOrchestrator(
                new FakePythonService(WriteFakeInterpreter(NeverCalledBatch)),
                s_indicatorChannelExporter, sourceSnapshotProvider: provider.Object);
            var progress = new SyncProgress<TrainingProgress>();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                orchestrator.StartTrainingAsync(config, progress, cancel.Token));

            Assert.NotNull(runDirectory);
            Assert.False(Directory.Exists(runDirectory));
            Assert.Contains(progress.Updates, update => update.Stage == "Cancelling");
            Assert.Contains(progress.Updates, update => update.Stage == "Cancelled");
            Assert.DoesNotContain(progress.Updates, update => update.Stage == "LateSnapshotting");
            Assert.Equal("Cancelled", progress.Updates[^1].Stage);
        }

        [Theory]
        [InlineData("Exporting")]
        [InlineData("Training")]
        [InlineData("Running")]
        public async Task StartTrainingAsync_PreparedCancelAtBoundary_NeverPublishesReadyAfterCancelling(string cancelAt)
        {
            var config = ValidConfig() with
            {
                Timeframe = TrainingTimeframe.Weekly,
                FeatureMode = PredictionFeatureMode.ComposedFeatures,
                FeatureSpec = new FeatureSpec { Channels = new[]
                {
                    new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA },
                } },
                SourceManifestPath = Path.Combine(_workDir, "manifest.json"),
            };
            string? runDirectory = null;
            var provider = new Mock<ITrainingSourceSnapshotProvider>();
            provider.Setup(p => p.PrepareAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<string>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
                .Returns((TrainingJobConfig job, string path, DateTimeOffset start,
                    IProgress<string> stage, CancellationToken token) =>
                {
                    runDirectory = path;
                    var dataset = Path.Combine(path, "dataset");
                    Directory.CreateDirectory(dataset);
                    var symbols = new Dictionary<string, PreparedTrainingSymbol>
                    {
                        ["TESTSYM"] = new(new string('A', 64), Path.Combine(path, "source.parquet"),
                            Path.Combine(dataset, "TESTSYM.parquet"), BuildIndicatorFixtureCandles()),
                    };
                    return Task.FromResult(new PreparedTrainingInput(path, dataset,
                        "provider", "revision", start.AddDays(-1), symbols));
                });
            using var cancel = new CancellationTokenSource();
            var progress = new SyncProgress<TrainingProgress>(update =>
            {
                if (update.Stage == cancelAt || (cancelAt == "Running" && update.Percent == 50))
                    cancel.Cancel();
            });
            var orchestrator = new TrainingOrchestrator(
                new FakePythonService(WriteFakeInterpreter(cancelAt == "Running"
                    ? PreparedLoopBatch : NeverCalledBatch)),
                s_indicatorChannelExporter, sourceSnapshotProvider: provider.Object);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                orchestrator.StartTrainingAsync(config, progress, cancel.Token));

            Assert.NotNull(runDirectory);
            Assert.False(Directory.Exists(runDirectory));
            var stages = progress.Updates.Select(update => update.Stage).ToArray();
            Assert.Contains("Cancelling", stages);
            Assert.Equal("Cancelled", stages[^1]);
            Assert.DoesNotContain("Ready", stages.SkipWhile(stage => stage != "Cancelling"));
        }

        [Fact]
        public async Task StartTrainingAsync_PreparedSuccess_ExportsBeforePythonAndRecordsProvenance()
        {
            var config = ValidConfig() with
            {
                Timeframe = TrainingTimeframe.Weekly,
                FeatureMode = PredictionFeatureMode.ComposedFeatures,
                FeatureSpec = new FeatureSpec { Channels = new[]
                {
                    new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close },
                    new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA },
                } },
                SourceManifestPath = Path.Combine(_workDir, "manifest.json"),
            };
            string? runDirectory = null;
            var provider = new Mock<ITrainingSourceSnapshotProvider>();
            provider.Setup(p => p.PrepareAsync(It.IsAny<TrainingJobConfig>(), It.IsAny<string>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<IProgress<string>>(), It.IsAny<CancellationToken>()))
                .Returns((TrainingJobConfig job, string path, DateTimeOffset start,
                    IProgress<string> stage, CancellationToken token) =>
                {
                    runDirectory = path;
                    stage.Report("Validating");
                    stage.Report("Snapshotting");
                    var dataset = Path.Combine(path, "dataset");
                    Directory.CreateDirectory(dataset);
                    var snapshot = Path.Combine(path, "snapshot", "TESTSYM.parquet");
                    Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
                    File.WriteAllText(snapshot, "fixture");
                    var symbols = new Dictionary<string, PreparedTrainingSymbol>
                    {
                        ["TESTSYM"] = new(new string('A', 64), snapshot,
                            Path.Combine(dataset, "TESTSYM.parquet"), BuildIndicatorFixtureCandles()),
                    };
                    return Task.FromResult(new PreparedTrainingInput(path, dataset,
                        "test-provider", "rev-42", start.AddDays(-1), symbols));
                });
            var orchestrator = new TrainingOrchestrator(
                new FakePythonService(WriteFakeInterpreter(PreparedCaptureSuccessBatch)),
                s_indicatorChannelExporter, sourceSnapshotProvider: provider.Object);
            var progress = new SyncProgress<TrainingProgress>();

            var result = await orchestrator.StartTrainingAsync(config, progress);

            Assert.True(result.Success);
            Assert.Equal("rev-42", result.SourceProvenance?.DatasetRevision);
            Assert.Equal(new string('A', 64), result.SourceProvenance?.Sha256BySymbol["TESTSYM"]);
            Assert.False(Directory.Exists(runDirectory));
            using var wire = JsonDocument.Parse(File.ReadAllText(Path.Combine(_workDir, "captured_prepared_config.json")));
            Assert.Equal("test-provider", wire.RootElement.GetProperty("source_provenance").GetProperty("provider_id").GetString());
            Assert.Equal("rev-42", wire.RootElement.GetProperty("source_provenance").GetProperty("dataset_revision").GetString());
            Assert.Equal(new string('A', 64), wire.RootElement.GetProperty("source_provenance")
                .GetProperty("sha256_by_symbol").GetProperty("TESTSYM").GetString());
            Assert.EndsWith("dataset", wire.RootElement.GetProperty("prepared_input_dir").GetString());
            Assert.Contains("TESTSYM", wire.RootElement.GetProperty("indicator_channel_export_paths")
                .EnumerateObject().Select(item => item.Name));
            var stages = progress.Updates.Select(update => update.Stage).Where(stage => stage is not null).ToList();
            Assert.Equal(new[] { "Validating", "Snapshotting", "Computing", "Exporting", "Ready", "Training", "Completed" },
                stages.Distinct().ToArray());
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
            "echo ARTIFACT:checkpoint:%~dp0run.json\r\n" +
            "echo STAGE:done\r\n" +
            "echo PROGRESS:100\r\n" +
            "exit /b 0\r\n";

        private const string PreparedCaptureSuccessBatch =
            "@echo off\r\n" +
            "copy %4 \"%~dp0captured_prepared_config.json\" >nul\r\n" +
            SuccessBatch;

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

        private const string PreparedLoopBatch =
            "@echo off\r\n" +
            "echo PROGRESS:50\r\n" +
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

        private const string CaptureResourceEnvironmentBatch =
            "@echo off\r\n" +
            "copy %4 \"%~dp0captured_resource_config.json\" >nul\r\n" +
            "echo %SA_MAX_COMPOSED_CHANNELS%,%SA_MAX_COMPOSED_TENSOR_SIZE_MB%,%SA_MAX_COMPOSED_BATCH_SAMPLES%,%SA_MAX_EVALUATION_FOLDS%,%SA_MAX_FEATURE_LAG%,%SA_MAX_RUN_DURATION_SECONDS%> \"%~dp0captured_limits.txt\"\r\n" +
            "echo STAGE:done\r\n" +
            "echo PROGRESS:100\r\n" +
            "exit /b 0\r\n";

        private const string CaptureBytecodeEnvironmentBatch =
            "@echo off\r\n" +
            "copy %4 \"%~dp0captured_bytecode_config.json\" >nul\r\n" +
            ">\"%~dp0captured_bytecode.txt\" echo [%PYTHONDONTWRITEBYTECODE%]\r\n" +
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

        private sealed class FixedResolver(string? path) : StockAnalyzer.Core.Execution.IPythonExecutableResolver
        {
            public bool TryResolve(out string executablePath)
            {
                executablePath = path ?? string.Empty;
                return path is not null;
            }
        }

        private sealed class SyncProgress<T> : IProgress<T>
        {
            private readonly Action<T>? _onReport;
            public SyncProgress(Action<T>? onReport = null) => _onReport = onReport;
            public List<T> Updates { get; } = new();
            public void Report(T value)
            {
                Updates.Add(value);
                _onReport?.Invoke(value);
            }
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
