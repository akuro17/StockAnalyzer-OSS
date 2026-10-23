using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services
{
    /// <summary>
    /// Runs the command that prints the installed pip version. The run has a time limit and a bound on the output kept, like the other check
    /// processes: a hung interpreter must not hold the sync until someone cancels it.
    /// </summary>
    internal static class PipVersionProbe
    {
        /// <summary>
        /// Returns the trimmed output of the process, or null when the version cannot be read (non-zero exit, no output, or the time limit
        /// <paramref name="timeout"/> passed). The process tree is killed on timeout or cancellation. Cancellation through
        /// <paramref name="cancellationToken"/> is rethrown. A process that cannot be started throws, as before.
        /// </summary>
        public static async Task<string?> ReadAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return null;

            using var cts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
            try
            {
                var stdoutTask = ProbeOutput.ReadBoundedAsync(process.StandardOutput, ProbeOutputLimits.PipVersionKeptChars, linkedCts.Token);
                var stderrTask = ProbeOutput.ReadBoundedAsync(process.StandardError, ProbeOutput.KeepNothing, linkedCts.Token);

                await process.WaitForExitAsync(linkedCts.Token);
                var output = (await stdoutTask).Trim();
                _ = await stderrTask;

                return process.ExitCode == 0 && output.Length > 0 ? output : null;
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                if (cancellationToken.IsCancellationRequested) throw;
                return null;
            }
        }
    }
}
