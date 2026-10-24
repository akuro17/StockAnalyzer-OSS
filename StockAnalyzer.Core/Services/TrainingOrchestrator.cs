using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Default <see cref="ITrainingOrchestrator"/>. Resolves the Python interpreter via
/// <see cref="IPythonService"/> and the orchestrator script via <see cref="PythonScriptLocator"/>,
/// writes the job config to a temp JSON file, and runs <c>run_training.py --config &lt;temp&gt;</c>
/// with the same <see cref="ProcessStartInfo"/> shape as
/// <see cref="PythonService.RunUpdatePipelineAsync(string?, IProgress{int}?, bool, CancellationToken)"/>
/// (UTF-8 redirected output, <c>PYTHONIOENCODING=utf-8</c>, no window).
/// </summary>
public sealed class TrainingOrchestrator : ITrainingOrchestrator
{
    private const string OrchestratorScriptRelativePath = "training/run_training.py";

    private readonly IPythonService _pythonService;
    private readonly IndicatorChannelExporter _indicatorChannelExporter;
    private readonly ILogger<TrainingOrchestrator> _logger;
    private readonly ITrainingResourceSettings? _resourceSettings;
    private readonly ITrainingSourceSnapshotProvider? _sourceSnapshotProvider;
    private readonly IModelGenerationRegistry? _modelGenerations;
    private readonly int _maxRunDurationSeconds;
    private readonly TrainingJobGate _jobGate;
    private readonly IPythonExecutableResolver? _executableResolver;

    public TrainingOrchestrator(
        IPythonService pythonService,
        IndicatorChannelExporter indicatorChannelExporter,
        ILogger<TrainingOrchestrator>? logger = null,
        ITrainingResourceSettings? resourceSettings = null,
        ITrainingSourceSnapshotProvider? sourceSnapshotProvider = null,
        IModelGenerationRegistry? modelGenerations = null, TrainingJobGate? jobGate = null,
        IPythonExecutableResolver? executableResolver = null)
    {
        _pythonService = pythonService ?? throw new ArgumentNullException(nameof(pythonService));
        _indicatorChannelExporter = indicatorChannelExporter ?? throw new ArgumentNullException(nameof(indicatorChannelExporter));
        _logger = logger ?? NullLogger<TrainingOrchestrator>.Instance;
        _resourceSettings = resourceSettings;
        _sourceSnapshotProvider = sourceSnapshotProvider;
        _modelGenerations = modelGenerations;
        _jobGate = jobGate ?? new TrainingJobGate();
        _executableResolver = executableResolver;
        _maxRunDurationSeconds = checked(TrainingResourceOverrides.MaximumRunDurationMinutes * 60);
    }

    internal TrainingOrchestrator(IPythonService pythonService, IndicatorChannelExporter indicatorChannelExporter,
        int maxRunDurationSeconds, ILogger<TrainingOrchestrator>? logger = null,
        ITrainingResourceSettings? resourceSettings = null,
        ITrainingSourceSnapshotProvider? sourceSnapshotProvider = null)
        : this(pythonService, indicatorChannelExporter, logger, resourceSettings, sourceSnapshotProvider)
    {
        if (maxRunDurationSeconds is < 1 or > TrainingResourceOverrides.MaximumRunDurationMinutes * 60)
            throw new ArgumentOutOfRangeException(nameof(maxRunDurationSeconds));
        _maxRunDurationSeconds = maxRunDurationSeconds;
    }

    public async Task<TrainingRunResult> StartTrainingAsync(
        TrainingJobConfig config,
        IProgress<TrainingProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (config is null)
        {
            throw new ArgumentNullException(nameof(config));
        }
        // The immutable record is captured before any await and stays fixed for this run.
        if (_resourceSettings?.LoadError is { } loadError)
            throw new InvalidOperationException(loadError);
        var resourceLimits = TrainingResourceGuard.Resolve(_resourceSettings?.Snapshot ?? new TrainingResourceOverrides())
            with { MaxRunDurationSeconds = _maxRunDurationSeconds };
        TrainingResourceGuard.Validate(config, resourceLimits);
        // Lists and maps inside the record are mutable. Freeze the complete wire
        // contract before the first await so later caller edits cannot change a run.
        config = TrainingConfigJson.DeserializeConfig(TrainingConfigJson.Serialize(config));

        // The lease survives every await, including process exit and cancellation cleanup.
        using var jobLease = _jobGate.Acquire(config);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(resourceLimits.MaxRunDurationSeconds));
        var runToken = timeout.Token;
        var runClock = Stopwatch.StartNew();
        var startedUtc = DateTimeOffset.UtcNow;
        // Generated once for new runs; strict Resume restores the original run identity.
        // Threaded through to Python via config.RunId below
        // instead of each side deriving its own timestamp independently (previous design: C#
        // used pre-launch UTC, Python used post-launch local time, so the two never matched and
        // Data/Experiments/<RunId>/ could not be cross-referenced against the .onnx filename by
        // name alone). The timestamp prefix stays human-readable; uniqueness itself does not
        // depend on clock resolution -- an 8-hex-char GUID suffix (the same "timestamp_GUID"
        // idiom already used for the temp config filename below) makes same-instant collisions
        // structurally impossible rather than merely unlikely.
        var runId = config.InitializationMode == TrainingInitializationMode.Resume
            ? TrainingCheckpointContract.ReadResumeRunId(config.CheckpointPath!)
            : config.ArtifactsDirectory is not null
                ? config.RunId! // an ephemeral run: the caller named the job folder after it (TrainingJobConfig.Validate requires and checks it)
                : TrainingRunId.New(startedUtc);
        using var parentLease = config.InitializationMode == TrainingInitializationMode.FineTune
            ? _modelGenerations?.Acquire(config.ParentModelId!)
                ?? throw new InvalidOperationException("FineTune parent model generation is unavailable.")
            : null;
        if (parentLease is not null)
            config = config with { ParentModelHash = _modelGenerations!.GetManifest(parentLease.ModelId)!.ModelHash };
        config = config with { RunId = runId };
        var stageGate = new object();
        var terminalStage = false;
        void ReportPrepared(TrainingProgress update)
        {
            lock (stageGate)
                if (!terminalStage && !runToken.IsCancellationRequested)
                    progress?.Report(update);
        }
        void ReportTerminal(string stage, bool requireNotCancelled = false)
        {
            lock (stageGate)
            {
                if (terminalStage) return;
                if (requireNotCancelled) runToken.ThrowIfCancellationRequested();
                terminalStage = true;
                progress?.Report(new TrainingProgress { Stage = stage });
            }
        }
        IProgress<TrainingProgress>? runProgress = config.RequiresFinalizedSource
            ? new InlineTrainingProgress(ReportPrepared) : progress;
        using var cancellationStage = config.RequiresFinalizedSource
            ? runToken.Register(() =>
            {
                lock (stageGate)
                    if (!terminalStage)
                        progress?.Report(new TrainingProgress { Stage = "Cancelling" });
            })
            : default;

        var preparedRunDirectory = config.RequiresFinalizedSource
            ? Path.Combine(Path.GetTempPath(), "sa_training_prepared_" + Guid.NewGuid().ToString("N")) : null;
        var ownsPreparedRunDirectory = false;
        RunState? processState = null;
        string? configPath = null;
        var keepEnsembleArtifacts = false;
        try
        {
        if (config.RequiresFinalizedSource)
        {
            if (_sourceSnapshotProvider is null)
                throw new InvalidOperationException("Training source snapshot provider is not configured.");
            runToken.ThrowIfCancellationRequested();
            if (Directory.Exists(preparedRunDirectory!))
                throw new IOException("Run-owned training directory already exists.");
            Directory.CreateDirectory(preparedRunDirectory!);
            ownsPreparedRunDirectory = true;
            IProgress<string> stage = new InlineStageProgress(name =>
                runProgress?.Report(new TrainingProgress { Stage = name }));
            var prepared = await _sourceSnapshotProvider.PrepareAsync(
                config, preparedRunDirectory!, startedUtc, stage, runToken).ConfigureAwait(false);
            runToken.ThrowIfCancellationRequested();
            var exports = await _indicatorChannelExporter.ExportPreparedAsync(
                config.FeatureSpec!, prepared, stage, runToken).ConfigureAwait(false);
            runToken.ThrowIfCancellationRequested();
            config = config with
            {
                PreparedInputDir = prepared.DatasetDirectory,
                IndicatorChannelExportPaths = exports,
                SourceProvenance = new TrainingSourceProvenance(prepared.ProviderId,
                    prepared.DatasetRevision, prepared.AsOfUtc,
                    prepared.Symbols.ToDictionary(p => p.Key, p => p.Value.SourceSha256,
                        StringComparer.OrdinalIgnoreCase)),
            };
            runProgress?.Report(new TrainingProgress { Stage = "Ready" });
        }

        // Indicator-kind ComposedFeatures channels have no Python-side formula (single formula
        // source: the ICoreIndicator implementations); export their per-symbol values now, before
        // run_training.py starts, so it can read them instead of re-deriving indicator math.
        // Price-only ComposedFeatures and every OhlcvMinMax run skip this (empty map, no-op).
        if (!config.RequiresFinalizedSource
            && config.FeatureMode == PredictionFeatureMode.ComposedFeatures
            && config.FeatureSpec is not null
            && config.FeatureSpec.Channels.Any(c => c.Kind == FeatureChannelKind.Indicator))
        {
            var indicatorChannelExportPaths = await _indicatorChannelExporter.ExportAsync(
                config.FeatureSpec, config.Symbols, config.Timeframe, runId, runToken).ConfigureAwait(false);
            config = config with { IndicatorChannelExportPaths = indicatorChannelExportPaths };
        }

        runToken.ThrowIfCancellationRequested();
        // A resolver only locates an installed interpreter; the service may initialise (and install) Python, which a caller that must not do so avoids.
        string pythonExe;
        if (_executableResolver is null)
            pythonExe = await _pythonService.ResolvePythonExecutablePathAsync(runToken).ConfigureAwait(false);
        else if (!_executableResolver.TryResolve(out pythonExe))
            throw new FileNotFoundException("The Python interpreter is not installed.");
        var scriptPath = PythonScriptLocator.Resolve(OrchestratorScriptRelativePath);

        var remaining = TimeSpan.FromSeconds(resourceLimits.MaxRunDurationSeconds) - runClock.Elapsed;
        if (remaining <= TimeSpan.Zero) timeout.Cancel();
        runToken.ThrowIfCancellationRequested();
        config = config with { ResourceLimits = resourceLimits, RemainingRunBudgetSeconds = remaining.TotalSeconds };

        configPath = Path.Combine(Path.GetTempPath(), $"sa_training_job_{runId}_{Guid.NewGuid():N}.json");
        // Utf8NoBom, not Encoding.UTF8: a BOM preamble breaks run_training.py's
        // json.loads(config_path.read_text(encoding="utf-8")) (bug: trainer exited with a
        // json.decoder.JSONDecodeError before doing anything).
        await File.WriteAllTextAsync(configPath, TrainingConfigJson.Serialize(config), TrainingConfigJson.Utf8NoBom, runToken)
            .ConfigureAwait(false);

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(scriptPath),
        };
        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.Environment[PythonChildEnvironment.IoEncodingVariable] = PythonChildEnvironment.IoEncodingValue;
        // An ephemeral run leaves nothing next to the scripts either: no bytecode cache. A normal run keeps its environment.
        if (config.ArtifactsDirectory is not null)
            startInfo.Environment[PythonChildEnvironment.NoBytecodeVariable] = PythonChildEnvironment.NoBytecodeValue;
        TrainingResourceGuard.ApplyToChild(startInfo, resourceLimits);

        var state = new RunState { Stage = config.RequiresFinalizedSource ? "Training" : null };
        processState = state;
        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) => HandleStdOutLine(e.Data, state, runProgress,
            config.RequiresFinalizedSource);
        process.ErrorDataReceived += (_, e) => HandleStdErrLine(e.Data, state);

        var processStarted = false;
        CancellationTokenRegistration registration = default;
        try
        {
            runToken.ThrowIfCancellationRequested();
            if (config.RequiresFinalizedSource)
                runProgress?.Report(new TrainingProgress { Stage = "Training" });
            runToken.ThrowIfCancellationRequested();
            _logger.LogInformation(
                "TrainingOrchestrator: starting run {RunId} ({Framework}/{Architecture}, {SymbolCount} symbol(s)).",
                runId, config.Framework, config.Architecture, config.Symbols.Length);

            if (!process.Start())
                throw new InvalidOperationException("Training process did not start.");
            processStarted = true;
            // Register only after Start. An already-cancelled token invokes the callback now,
            // closing the narrow cancellation window between the pre-start check and Start.
            registration = runToken.Register(() => TryKillProcess(process));
            // Best-effort OS-level safety net: if this app is killed abruptly (crash, Task
            // Manager) rather than shut down gracefully, Windows closes our Job Object handle as
            // part of process teardown, which kills this training subprocess too instead of
            // leaving an orphaned python.exe behind. No-op on non-Windows or on failure (see
            // WindowsJobObject.TryAssign). Mirrors PythonProcessManager's use of the same guard
            // for its long-lived Python IPC process.
            using var jobHandle = WindowsJobObject.TryAssign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(runToken).ConfigureAwait(false);
            runToken.ThrowIfCancellationRequested();
        }
        finally
        {
            registration.Dispose();
            if (processStarted)
            {
                if (!process.HasExited)
                    TryKillProcess(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                process.WaitForExit(); // Complete redirected DataReceived callbacks before cleanup.
            }
        }

        var completedUtc = DateTimeOffset.UtcNow;
        var success = process.ExitCode == 0
            && state.OnnxArtifactPath is not null
            && File.Exists(state.OnnxArtifactPath);
        runToken.ThrowIfCancellationRequested();
        lock (state.Lock) { state.Terminal = true; }

        _logger.LogInformation(
            "TrainingOrchestrator: run {RunId} finished with exit code {ExitCode} (success={Success}).",
            runId, process.ExitCode, success);

        if (config.RequiresFinalizedSource)
            ReportTerminal(success ? "Completed" : "Failed", requireNotCancelled: true);
        keepEnsembleArtifacts = success;
        return new TrainingRunResult
        {
            RunId = runId,
            Success = success,
            ExitCode = process.ExitCode,
            OnnxArtifactPath = state.OnnxArtifactPath,
            MetricsArtifactPath = state.MetricsArtifactPath,
            CheckpointManifestPath = state.CheckpointManifestPath,
            EnsembleManifestPath = state.EnsembleManifestPath,
            Metrics = state.LastMetric ?? new Dictionary<string, double>(),
            Message = success ? null : BuildFailureMessage(process.ExitCode, state),
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            SourceProvenance = config.SourceProvenance,
            ResourceLimits = resourceLimits,
        };
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            if (processState is not null)
                lock (processState.Lock) { processState.Terminal = true; }
            if (preparedRunDirectory is not null)
                ReportTerminal("Failed");
            throw new TimeoutException($"Training exceeded {resourceLimits.MaxRunDurationSeconds} seconds.", ex);
        }
        catch (OperationCanceledException)
        {
            if (processState is not null)
                lock (processState.Lock) { processState.Terminal = true; }
            if (preparedRunDirectory is not null)
                ReportTerminal("Cancelled");
            throw;
        }
        catch
        {
            if (processState is not null)
                lock (processState.Lock) { processState.Terminal = true; }
            if (preparedRunDirectory is not null)
                ReportTerminal("Failed");
            throw;
        }
        finally
        {
            // The trainer has exited (or been killed) by now. A run that did not succeed leaves none of its ensemble files behind.
            if (!keepEnsembleArtifacts && processState is not null)
                DeleteEnsembleArtifacts(processState, runId);
            if (configPath is not null)
                TryDeleteFile(configPath);
            if (ownsPreparedRunDirectory && preparedRunDirectory is not null && Directory.Exists(preparedRunDirectory))
            {
                try { Directory.Delete(preparedRunDirectory, recursive: true); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(cleanupError,
                        "TrainingOrchestrator: run-owned directory remains at {Path}", preparedRunDirectory);
                }
            }
        }
    }

    private void HandleStdOutLine(string? line, RunState state, IProgress<TrainingProgress>? progress,
        bool prepared = false)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        _logger.LogDebug("TrainingOrchestrator[stdout]: {Line}", line);

        if (TrainingProtocolLine.TryParseStage(line, out var stage))
        {
            if (!prepared)
            {
                lock (state.Lock) { state.Stage = stage; }
                ReportProgress(state, progress);
            }
            return;
        }

        if (TrainingProtocolLine.TryParsePercent(line, out var percent))
        {
            lock (state.Lock) { state.Percent = percent; }
            ReportProgress(state, progress);
            return;
        }

        if (TrainingProtocolLine.TryParseMetric(line, out var metric))
        {
            lock (state.Lock) { state.LastMetric = metric; }
            ReportProgress(state, progress);
            return;
        }

        if (TrainingProtocolLine.TryParseArtifact(line, out var kind, out var path))
        {
            lock (state.Lock)
            {
                if (string.Equals(kind, "onnx", StringComparison.OrdinalIgnoreCase))
                {
                    state.OnnxArtifactPath = path;
                }
                else if (string.Equals(kind, "metrics", StringComparison.OrdinalIgnoreCase))
                {
                    state.MetricsArtifactPath = path;
                }
                else if (string.Equals(kind, "checkpoint", StringComparison.OrdinalIgnoreCase))
                    state.CheckpointManifestPath = path;
                else if (string.Equals(kind, "ensemble", StringComparison.OrdinalIgnoreCase))
                    state.EnsembleManifestPath = path;
                else if (string.Equals(kind, "ensemble_member", StringComparison.OrdinalIgnoreCase))
                    state.EnsembleMemberPaths.Add(path);
                else if (string.Equals(kind, "ensemble_checkpoints", StringComparison.OrdinalIgnoreCase))
                    state.EnsembleCheckpointRoot = path;
            }
            ReportProgress(state, progress);
        }
    }

    private void HandleStdErrLine(string? line, RunState state)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        _logger.LogWarning("TrainingOrchestrator[stderr]: {Line}", line);
        lock (state.Lock) { state.LastStdErrLine = line; }
    }

    private static void ReportProgress(RunState state, IProgress<TrainingProgress>? progress)
    {
        if (progress is null)
        {
            return;
        }

        TrainingProgress snapshot;
        lock (state.Lock)
        {
            if (state.Terminal) return;
            snapshot = new TrainingProgress
            {
                Stage = state.Stage,
                Percent = state.Percent,
                Metric = state.LastMetric,
                CheckpointManifestPath = state.CheckpointManifestPath,
            };
        }
        progress.Report(snapshot);
    }

    private static string BuildFailureMessage(int exitCode, RunState state)
    {
        lock (state.Lock)
        {
            return state.LastStdErrLine is { Length: > 0 }
                ? $"Trainer exited with code {exitCode}: {state.LastStdErrLine}"
                : $"Trainer exited with code {exitCode}.";
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temp config file is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; a leftover temp config file is harmless.
        }
    }

    /// <summary>
    /// Rolls an ensemble run that did not succeed back: removes every ensemble member the trainer announced
    /// (<c>ARTIFACT:ensemble_member:&lt;onnx&gt;</c>) together with its sidecars (<c>&lt;onnx&gt;.*</c>) and the ensemble manifest
    /// (<c>ARTIFACT:ensemble:&lt;path&gt;</c>), and the members' checkpoint directories (<c>&lt;checkpoint root&gt;/&lt;run id&gt;-mNN</c>
    /// under the root announced by <c>ARTIFACT:ensemble_checkpoints:&lt;dir&gt;</c>; only directories of this run's own id), so a failed
    /// run leaves none of the files it produced. The host owns this cleanup, so it also works when the trainer was killed. A deletion
    /// that fails is logged and never replaces the run's own outcome.
    /// </summary>
    /// <remarks>
    /// The announced paths come from the trainer's text protocol, so a delete is confined to what the run owns
    /// (<see cref="TrainingEnsembleArtifactLayout"/>): a member is a fully qualified <c>&lt;stem&gt;_m&lt;digits&gt;.onnx</c>; the
    /// artifact directory is the one of the first such member; the manifest and every further member must be files of that directory;
    /// the checkpoint root must be its <see cref="TrainingCheckpointContract.DirectoryName"/> subdirectory. Anything else is skipped and logged.
    /// </remarks>
    private void DeleteEnsembleArtifacts(RunState state, string runId)
    {
        string[] announced;
        string? manifest;
        string? checkpointRoot;
        lock (state.Lock)
        {
            announced = state.EnsembleMemberPaths.ToArray();
            manifest = state.EnsembleManifestPath;
            checkpointRoot = state.EnsembleCheckpointRoot;
        }

        var directory = announced.Where(IsOwnedMemberPath).Select(Path.GetDirectoryName).FirstOrDefault();
        if (directory is null)
        {
            if (announced.Length > 0 || manifest is not null || checkpointRoot is not null)
                _logger.LogWarning("TrainingOrchestrator: no announced ensemble member is a valid member file; nothing was removed.");
            return;
        }

        if (checkpointRoot is not null)
        {
            if (IsSamePath(checkpointRoot, Path.Combine(directory, TrainingCheckpointContract.DirectoryName)))
                DeleteMemberCheckpointDirectories(checkpointRoot, runId);
            else
                _logger.LogWarning("TrainingOrchestrator: skipped the announced checkpoint root {Root}: it is not {Directory}/{Name}.",
                    checkpointRoot, directory, TrainingCheckpointContract.DirectoryName);
        }

        if (manifest is not null)
        {
            if (Path.IsPathFullyQualified(manifest) && IsSamePath(Path.GetDirectoryName(manifest), directory)
                && manifest.EndsWith(TrainingEnsembleManifest.FileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(manifest); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(error, "TrainingOrchestrator: could not remove the ensemble manifest {Manifest}", manifest);
                }
            }
            else
            {
                _logger.LogWarning("TrainingOrchestrator: skipped the announced ensemble manifest {Manifest}: it is not an ensemble manifest in {Directory}.",
                    manifest, directory);
            }
        }

        foreach (var member in announced)
        {
            if (!IsOwnedMemberPath(member) || !IsSamePath(Path.GetDirectoryName(member), directory))
            {
                _logger.LogWarning("TrainingOrchestrator: skipped the announced ensemble member {Member}: it is not a member file in {Directory}.",
                    member, directory);
                continue;
            }

            var name = Path.GetFileName(member);
            try
            {
                var files = Directory.EnumerateFiles(directory, name + ".*")
                    .Where(f => Path.GetFileName(f).StartsWith(name + ".", StringComparison.Ordinal)).ToList();
                files.Add(member);
                foreach (var file in files)
                    File.Delete(file); // a missing file is not an error
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(error, "TrainingOrchestrator: could not remove the artifacts of ensemble member {Member}", member);
            }
        }
    }

    private static bool IsOwnedMemberPath(string path) =>
        Path.IsPathFullyQualified(path) && TrainingEnsembleArtifactLayout.IsMemberFileName(Path.GetFileName(path));

    private static bool IsSamePath(string? left, string? right) =>
        left is not null && right is not null
        && string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>Deletes <c>&lt;root&gt;/&lt;runId&gt;-m&lt;digits&gt;</c> directories only (never another run's, never a link target).</summary>
    private void DeleteMemberCheckpointDirectories(string? checkpointRoot, string runId)
    {
        if (string.IsNullOrEmpty(checkpointRoot) || string.IsNullOrEmpty(runId) || !Directory.Exists(checkpointRoot))
            return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(checkpointRoot).ToList())
            {
                if (!TrainingEnsembleArtifactLayout.IsMemberCheckpointDirectory(Path.GetFileName(directory), runId)
                    || new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                try { Directory.Delete(directory, recursive: true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(error, "TrainingOrchestrator: could not remove the checkpoint directory {Directory}", directory);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "TrainingOrchestrator: could not list the checkpoint root {Root}", checkpointRoot);
        }
    }

    private void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exit raced the cancellation callback.
        }
        catch (Win32Exception error)
        {
            _logger.LogWarning(error, "TrainingOrchestrator: could not terminate the training process.");
        }
    }

    private sealed class RunState
    {
        public readonly object Lock = new();
        public string? Stage;
        public int Percent;
        public string? CheckpointManifestPath;
        public string? EnsembleManifestPath;
        public readonly List<string> EnsembleMemberPaths = new();
        public string? EnsembleCheckpointRoot;
        public IReadOnlyDictionary<string, double>? LastMetric;
        public string? OnnxArtifactPath;
        public string? MetricsArtifactPath;
        public string? LastStdErrLine;
        public bool Terminal;
    }

    private sealed class InlineStageProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private sealed class InlineTrainingProgress(Action<TrainingProgress> report) : IProgress<TrainingProgress>
    {
        public void Report(TrainingProgress value) => report(value);
    }
}

/// <summary>
/// Pure parser for the <c>run_training.py</c> stdout line protocol (<c>STAGE:</c> /
/// <c>PROGRESS:</c> / <c>METRIC:</c> / <c>ARTIFACT:</c>), kept separate from
/// <see cref="TrainingOrchestrator"/> so the parsing rules are unit-testable without spawning a
/// process.
/// </summary>
internal static class TrainingProtocolLine
{
    public static bool TryParseStage(string line, out string stage)
    {
        if (line.StartsWith("STAGE:", StringComparison.Ordinal))
        {
            stage = line["STAGE:".Length..];
            return true;
        }

        stage = "";
        return false;
    }

    public static bool TryParsePercent(string line, out int percent)
    {
        if (line.StartsWith("PROGRESS:", StringComparison.Ordinal)
            && int.TryParse(line["PROGRESS:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            percent = Math.Clamp(value, 0, 100);
            return true;
        }

        percent = 0;
        return false;
    }

    public static bool TryParseMetric(string line, out IReadOnlyDictionary<string, double>? metric)
    {
        metric = null;
        if (!line.StartsWith("METRIC:", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            metric = JsonSerializer.Deserialize<Dictionary<string, double>>(line["METRIC:".Length..]);
            return metric is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseArtifact(string line, out string kind, out string path)
    {
        kind = "";
        path = "";
        if (!line.StartsWith("ARTIFACT:", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = line["ARTIFACT:".Length..];
        var separator = rest.IndexOf(':');
        if (separator <= 0 || separator == rest.Length - 1)
        {
            return false;
        }

        kind = rest[..separator];
        path = rest[(separator + 1)..];
        return true;
    }
}
