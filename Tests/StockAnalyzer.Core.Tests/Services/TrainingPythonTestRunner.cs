using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

internal static class TrainingPythonTestRunner
{
    private const int MaxDiagnosticCharactersPerStream = 64 * 1024;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    internal static async Task<string> RunAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        start.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(start);
        Assert.NotNull(process);
        using var drainCancellation = new CancellationTokenSource();
        var stdout = new BoundedOutputCapture();
        var stderr = new BoundedOutputCapture();
        var stdoutDrain = stdout.ReadAsync(process.StandardOutput, drainCancellation.Token);
        var stderrDrain = stderr.ReadAsync(process.StandardError, drainCancellation.Token);
        var completion = Task.WhenAll(process.WaitForExitAsync(), stdoutDrain, stderrDrain);
        try
        {
            await completion.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            string? cleanupFailure = null;
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception error) { cleanupFailure = $"\nprocess kill: {error}"; }
            }
            drainCancellation.Cancel();
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            try { await completion.WaitAsync(CleanupTimeout); }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException
                or IOException or ObjectDisposedException or InvalidOperationException or Win32Exception)
            {
                cleanupFailure += $"\ncleanup: {error}";
            }
            throw new Xunit.Sdk.XunitException(
                $"Python timed out after {timeout}.\nstdout:\n{stdout.Snapshot}\nstderr:\n{stderr.Snapshot}{cleanupFailure}");
        }

        var diagnostics = $"Python exit {process.ExitCode}\nstdout:\n{stdout.Snapshot}\nstderr:\n{stderr.Snapshot}";
        Assert.True(process.ExitCode == 0, diagnostics);
        return stdout.Snapshot;
    }

    private sealed class BoundedOutputCapture
    {
        private readonly object _gate = new();
        private readonly StringBuilder _tail = new();

        public string Snapshot
        {
            get { lock (_gate) return _tail.ToString(); }
        }

        public async Task ReadAsync(StreamReader stream, CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            try
            {
                int count;
                while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                {
                    lock (_gate)
                    {
                        _tail.Append(buffer, 0, count);
                        if (_tail.Length > MaxDiagnosticCharactersPerStream)
                            _tail.Remove(0, _tail.Length - MaxDiagnosticCharactersPerStream);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
            catch (IOException) when (cancellationToken.IsCancellationRequested) { }
        }
    }
}
