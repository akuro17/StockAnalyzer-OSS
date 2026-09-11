using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<TrainingOrchestrator> _logger;

    public TrainingOrchestrator(IPythonService pythonService, ILogger<TrainingOrchestrator>? logger = null)
    {
        _pythonService = pythonService ?? throw new ArgumentNullException(nameof(pythonService));
        _logger = logger ?? NullLogger<TrainingOrchestrator>.Instance;
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
        config.Validate();

        var startedUtc = DateTimeOffset.UtcNow;
        // Generated exactly once, here, and threaded through to Python via config.RunId below
        // instead of each side deriving its own timestamp independently (previous design: C#
        // used pre-launch UTC, Python used post-launch local time, so the two never matched and
        // Data/Experiments/<RunId>/ could not be cross-referenced against the .onnx filename by
        // name alone). The timestamp prefix stays human-readable; uniqueness itself does not
        // depend on clock resolution -- an 8-hex-char GUID suffix (the same "timestamp_GUID"
        // idiom already used for the temp config filename below) makes same-instant collisions
        // structurally impossible rather than merely unlikely.
        var runId = $"{startedUtc.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..8]}";
        config = config with { RunId = runId };

        var pythonExe = await _pythonService.ResolvePythonExecutablePathAsync(ct).ConfigureAwait(false);
        var scriptPath = PythonScriptLocator.Resolve(OrchestratorScriptRelativePath);

        var configPath = Path.Combine(Path.GetTempPath(), $"sa_training_job_{runId}_{Guid.NewGuid():N}.json");
        // Utf8NoBom, not Encoding.UTF8: a BOM preamble breaks run_training.py's
        // json.loads(config_path.read_text(encoding="utf-8")) (bug: trainer exited with a
        // json.decoder.JSONDecodeError before doing anything).
        await File.WriteAllTextAsync(configPath, TrainingConfigJson.Serialize(config), TrainingConfigJson.Utf8NoBom, ct)
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
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        var state = new RunState();
        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) => HandleStdOutLine(e.Data, state, progress);
        process.ErrorDataReceived += (_, e) => HandleStdErrLine(e.Data, state);

        // Cancellation kills the whole process tree; run_training.py has no children of its own
        // to worry about (the trainer runs in-process), but this still guards a future trainer
        // that shells out. Matches PythonService.RunUpdatePipelineAsync's cancellation contract.
        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Process already exited between the HasExited check and Kill; nothing to do.
            }
        });

        try
        {
            _logger.LogInformation(
                "TrainingOrchestrator: starting run {RunId} ({Framework}/{Architecture}, {SymbolCount} symbol(s)).",
                runId, config.Framework, config.Architecture, config.Symbols.Length);

            process.Start();
            // Best-effort OS-level safety net: if this app is killed abruptly (crash, Task
            // Manager) rather than shut down gracefully, Windows closes our Job Object handle as
            // part of process teardown, which kills this training subprocess too instead of
            // leaving an orphaned python.exe behind. No-op on non-Windows or on failure (see
            // WindowsJobObject.TryAssign). Mirrors PythonProcessManager's use of the same guard
            // for its long-lived Python IPC process.
            using var jobHandle = WindowsJobObject.TryAssign(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(configPath);
        }

        var completedUtc = DateTimeOffset.UtcNow;
        var success = process.ExitCode == 0
            && state.OnnxArtifactPath is not null
            && File.Exists(state.OnnxArtifactPath);

        _logger.LogInformation(
            "TrainingOrchestrator: run {RunId} finished with exit code {ExitCode} (success={Success}).",
            runId, process.ExitCode, success);

        return new TrainingRunResult
        {
            RunId = runId,
            Success = success,
            ExitCode = process.ExitCode,
            OnnxArtifactPath = state.OnnxArtifactPath,
            MetricsArtifactPath = state.MetricsArtifactPath,
            Metrics = state.LastMetric ?? new Dictionary<string, double>(),
            Message = success ? null : BuildFailureMessage(process.ExitCode, state),
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
        };
    }

    private void HandleStdOutLine(string? line, RunState state, IProgress<TrainingProgress>? progress)
    {
        if (string.IsNullOrEmpty(line))
        {
            return;
        }

        _logger.LogDebug("TrainingOrchestrator[stdout]: {Line}", line);

        if (TrainingProtocolLine.TryParseStage(line, out var stage))
        {
            lock (state.Lock) { state.Stage = stage; }
            ReportProgress(state, progress);
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
            }
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
            snapshot = new TrainingProgress
            {
                Stage = state.Stage,
                Percent = state.Percent,
                Metric = state.LastMetric,
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

    private sealed class RunState
    {
        public readonly object Lock = new();
        public string? Stage;
        public int Percent;
        public IReadOnlyDictionary<string, double>? LastMetric;
        public string? OnnxArtifactPath;
        public string? MetricsArtifactPath;
        public string? LastStdErrLine;
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
