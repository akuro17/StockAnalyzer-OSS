using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Execution;

/// <summary>What the managed reinforcement-learning environment answered: usable, or not (and which Python module could not be imported, when the interpreter said so).</summary>
internal sealed record RlEnvironmentStatus(bool Available, string? MissingModule = null)
{
    public static RlEnvironmentStatus Usable { get; } = new(true);

    public static RlEnvironmentStatus Unusable(string? missingModule = null) => new(false, missingModule);
}

/// <summary>How one probe process ended: it ran out of time, or it exited with this code and this (bounded) standard-error text.</summary>
internal sealed record RlProbeRun(bool TimedOut, int ExitCode, string StandardError);

/// <summary>Runs the probe process (the seam of the tests); a real run is <see cref="RlEnvironmentProbe.RunProcessAsync"/>.</summary>
internal delegate Task<RlProbeRun> RlProbeProcessRunner(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken);

/// <summary>
/// Asks the managed reinforcement-learning interpreter (the application's own <c>.venv</c>) whether the packages the trainer imports can be imported, so a
/// start is refused before any job or folder exists instead of failing seconds into the run. Only a success is kept: a failure is asked again next time, so
/// a user who installs the packages needs no restart. Callers that arrive while a probe runs share its answer (one process), and the answer is never
/// produced under a job-table lock (the probe runs before any job exists).
/// </summary>
internal sealed class RlEnvironmentProbe
{
    /// <summary>The check: the two packages the RL trainer needs beyond the standard library.</summary>
    internal const string ImportStatement = "import gymnasium, stable_baselines3";

    // Python's own text for an import failure; the name is accepted only as a plain dotted identifier, so it can never carry a path.
    private static readonly Regex MissingModuleLine = new(@"No module named '(?<name>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z0-9_]+)*)'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private readonly Func<(string Python, string WorkingDirectory)> _locate;
    private readonly RlProbeProcessRunner _run;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private Task<RlEnvironmentStatus>? _inFlight;
    private volatile bool _verified;

    /// <param name="locate">The interpreter and the folder to start it in; may throw <see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/> when the Python sources themselves are missing.</param>
    /// <param name="run">Null runs a real process.</param>
    /// <param name="timeout">Null is <see cref="ExecutionLimits.RlEnvironmentProbeSeconds"/>.</param>
    public RlEnvironmentProbe(Func<(string Python, string WorkingDirectory)> locate, RlProbeProcessRunner? run = null, TimeSpan? timeout = null)
    {
        _locate = locate;
        _run = run ?? RunProcessAsync;
        _timeout = timeout ?? TimeSpan.FromSeconds(ExecutionLimits.RlEnvironmentProbeSeconds);
    }

    /// <summary>Waiting is cancellable; the probe itself is not (it is shared and has its own time limit).</summary>
    public async Task<RlEnvironmentStatus> CheckAsync(CancellationToken cancellationToken)
    {
        (string python, string workingDirectory) location;
        try
        {
            location = _locate();
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return RlEnvironmentStatus.Unusable(); // the Python sources themselves cannot be found: there is no environment to speak of
        }

        if (!File.Exists(location.python))
        {
            return RlEnvironmentStatus.Unusable();
        }

        Task<RlEnvironmentStatus> probe;
        lock (_gate)
        {
            if (_verified)
            {
                return RlEnvironmentStatus.Usable;
            }

            // Task.Run: the body cannot finish (and clear the field) before this lock is released, so a finished probe is never left in the field.
            _inFlight ??= Task.Run(() => ProbeAsync(location.python, location.workingDirectory));
            probe = _inFlight;
        }

        return await probe.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<RlEnvironmentStatus> ProbeAsync(string python, string workingDirectory)
    {
        try
        {
            RlProbeRun run = await _run(CreateStartInfo(python, workingDirectory), _timeout, CancellationToken.None).ConfigureAwait(false);
            if (!run.TimedOut && run.ExitCode == 0)
            {
                _verified = true;
                return RlEnvironmentStatus.Usable;
            }

            return RlEnvironmentStatus.Unusable(run.TimedOut ? null : MissingModuleOf(run.StandardError));
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
        {
            return RlEnvironmentStatus.Unusable(); // the interpreter cannot be started
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = null;
            }
        }
    }

    internal static string? MissingModuleOf(string standardError)
    {
        Match match = MissingModuleLine.Match(standardError);
        if (!match.Success)
        {
            return null;
        }

        string name = match.Groups["name"].Value.Split('.')[0];
        return name.Length <= ExecutionLimits.RlProbeModuleNameChars ? name : null;
    }

    private static ProcessStartInfo CreateStartInfo(string python, string workingDirectory)
    {
        // Every standard stream is redirected: the MCP server's own stdin and stdout are the protocol and must never reach the child.
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(ImportStatement);
        start.Environment[PythonChildEnvironment.IoEncodingVariable] = PythonChildEnvironment.IoEncodingValue;
        start.Environment[PythonChildEnvironment.NoBytecodeVariable] = PythonChildEnvironment.NoBytecodeValue; // an import must not write into the environment
        return start;
    }

    /// <summary>Starts the process, discards its output, keeps a bounded part of its error text, and kills it (with its children) when it outlives <paramref name="timeout"/>.</summary>
    internal static async Task<RlProbeRun> RunProcessAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = start };
        process.Start();
        using var jobObject = WindowsJobObject.TryAssign(process); // best effort: the probe dies with this process even when it is killed before it can clean up
        process.StandardInput.Close();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        Task<string> error = ReadBoundedAsync(process.StandardError);
        Task output = DiscardAsync(process.StandardOutput);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            // Bounded: a process that survives the kill must not hold the caller. The readers end when the pipes close, so they are awaited inside the same bound.
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(ExecutionLimits.CancelGraceSeconds));
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(reap.Token), error, output).WaitAsync(reap.Token).ConfigureAwait(false);
            }
            catch (Exception reapError) when (reapError is OperationCanceledException or IOException)
            {
                // left behind on purpose: the answer is already known (timed out)
            }

            return new RlProbeRun(TimedOut: true, -1, string.Empty);
        }

        await Task.WhenAll(error, output).ConfigureAwait(false);
        return new RlProbeRun(TimedOut: false, process.ExitCode, error.Result);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            text.Append(buffer, 0, Math.Min(read, Math.Max(0, ExecutionLimits.RlProbeStandardErrorChars - text.Length))); // keeps draining past the cap
        }

        return text.ToString();
    }

    private static async Task DiscardAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
        {
        }
    }
}
