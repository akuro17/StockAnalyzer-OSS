using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Polly;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Answers one framed worker request; the trainers pass their IPC host's <c>Handle</c>.</summary>
internal delegate object RlWorkerRequestHandler(JsonElement request, CancellationToken cancellationToken);

/// <summary>
/// The one owner of the isolated RL interpreter process for every trainer (T15/T16 and the portfolio program): managed-environment start,
/// resource limits, job object, bounded diagnostics log, framed request/response pump and the grace-then-kill shutdown.
/// </summary>
internal static class RlWorkerProcess
{
    /// <summary>
    /// The one place that locates a script of the Python tree and the managed environment's interpreter beside it
    /// (<c>.venv/Scripts/python.exe</c> on Windows, <c>.venv/bin/python</c> elsewhere). The interpreter file is not checked for existence.
    /// </summary>
    internal static (string Script, string PythonRoot, string Python) ResolveInterpreter(string relativeScript)
    {
        string script = PythonScriptLocator.Resolve(relativeScript);
        string pythonRoot = Directory.GetParent(Path.GetDirectoryName(script)!)!.FullName;
        string python = Path.Combine(pythonRoot, RlFiles.VirtualEnvironment,
            OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");
        return (script, pythonRoot, python);
    }

    /// <summary>Runs one script of the managed environment. <paramref name="handler"/> is null for workers that must not speak the protocol.</summary>
    /// <param name="beforeStart">Runs after the cancellation check and immediately before the process starts (strict identity verification).</param>
    /// <param name="started">Receives the process id immediately after a successful start.</param>
    public static async Task RunAsync(string relativeScript, string configPath, string directory, TrainingResourceLimits limits,
        RlWorkerRequestHandler? handler, CancellationToken ct, Action? beforeStart = null, Action<int>? started = null)
    {
        var (script, pythonRoot, python) = ResolveInterpreter(relativeScript);
        if (!File.Exists(python)) throw new FileNotFoundException("Managed RL virtual environment is unavailable.", python);
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = pythonRoot,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false, true),
        };
        start.ArgumentList.Add("-u"); start.ArgumentList.Add(script); start.ArgumentList.Add("--config"); start.ArgumentList.Add(configPath);
        start.Environment[PythonChildEnvironment.IoEncodingVariable] = PythonChildEnvironment.IoEncodingValue;
        start.Environment[PythonChildEnvironment.NoBytecodeVariable] = PythonChildEnvironment.NoBytecodeValue;
        TrainingResourceGuard.ApplyToChild(start, limits);
        await PumpAsync(start, Path.Combine(directory, Path.GetFileNameWithoutExtension(script) + ".stderr.log"), handler, beforeStart, started, ct).ConfigureAwait(false);
    }

    /// <summary>Starts <paramref name="start"/> and serves it until it exits; the seam for tests with an arbitrary interpreter and script.</summary>
    internal static async Task PumpAsync(ProcessStartInfo start, string stderrPath, RlWorkerRequestHandler? handler,
        Action? beforeStart, Action<int>? started, CancellationToken ct)
    {
        using var process = new Process { StartInfo = start };
        using var stderr = new StreamWriter(stderrPath, false, TrainingConfigJson.Utf8NoBom);
        var requests = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(RlContract.IpcTimeoutSeconds)).Build();
        ct.ThrowIfCancellationRequested();
        beforeStart?.Invoke();
        if (!process.Start()) throw new InvalidOperationException("RL interpreter did not start.");
        started?.Invoke(process.Id);
        using var jobObject = WindowsJobObject.TryAssign(process);
        var diagnostics = DrainDiagnosticsAsync(process.StandardError, stderr, ct);
        try
        {
            var reader = new BoundedLineReader(process.StandardOutput.BaseStream, RlContract.IpcFrameByteLimit, strict: true);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (handler is null) throw new InvalidDataException("Source worker wrote unexpected protocol output.");
                using var frame = JsonDocument.Parse(line);
                await requests.ExecuteAsync(async requestToken =>
                {
                    object response = handler(frame.RootElement, requestToken);
                    string encoded = JsonSerializer.Serialize(response, RlFiles.Json);
                    if (Encoding.UTF8.GetByteCount(encoded) >= RlContract.IpcFrameByteLimit) throw new InvalidDataException("RL response exceeds frame bound.");
                    await process.StandardInput.WriteLineAsync(encoded.AsMemory(), requestToken).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(requestToken).ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await diagnostics.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"RL worker exited {process.ExitCode}; see {(stderr.BaseStream is FileStream file ? file.Name : stderrPath)}.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(RlContract.CancellationTimeoutSeconds));
                try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(RlContract.CancellationTimeoutSeconds));
                    await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                }
            }
            try { await diagnostics.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static async Task DrainDiagnosticsAsync(StreamReader reader, StreamWriter output, CancellationToken ct)
    {
        var bounded = new BoundedLineReader(reader.BaseStream, RlContract.IpcFrameByteLimit);
        long bytes = 0;
        while (await bounded.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            bytes = checked(bytes + Encoding.UTF8.GetByteCount(line));
            if (bytes <= RlContract.BufferByteLimit) await output.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        }
        await output.FlushAsync(ct).ConfigureAwait(false);
    }
}
