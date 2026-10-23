using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services
{
    /// <summary>Result of an import check of an installed package.</summary>
    internal enum ModuleInstallState
    {
        /// <summary>The import succeeded.</summary>
        Installed,

        /// <summary>The import ran and failed (non-zero exit): treated as not installed.</summary>
        Missing,

        /// <summary>The check could not be run (for example the process could not start).</summary>
        Failed,

        /// <summary>The import did not finish within the time limit.</summary>
        TimedOut
    }

    /// <summary>
    /// Runs an import check and classifies the outcome. A check that timed out or failed says nothing about whether the
    /// package is installed, so it must not lead to an install or upgrade (that would replace a newer version with the pin).
    /// </summary>
    internal static class ModuleInstallProbe
    {
        /// <summary>Maps the outcome of a finished or aborted check to its state.</summary>
        public static ModuleInstallState Classify(bool startFailed, bool timedOut, int? exitCode)
        {
            if (timedOut) return ModuleInstallState.TimedOut;
            if (startFailed || exitCode is null) return ModuleInstallState.Failed;
            return exitCode == 0 ? ModuleInstallState.Installed : ModuleInstallState.Missing;
        }

        /// <summary>True when the state tells whether the package is installed, so the install step may decide.</summary>
        public static bool IsConclusive(ModuleInstallState state)
            => state is ModuleInstallState.Installed or ModuleInstallState.Missing;

        /// <summary>
        /// Starts the process, waits up to <paramref name="timeout"/>, and classifies the result. The process tree is killed on timeout or
        /// cancellation. Only the internal timeout is <see cref="ModuleInstallState.TimedOut"/>; cancellation through
        /// <paramref name="cancellationToken"/> is rethrown (N-07).
        /// </summary>
        public static async Task<ModuleInstallState> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            try
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
                if (process == null) return Classify(startFailed: true, timedOut: false, exitCode: null);

                using var cts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                try
                {
                    // Only the exit code is used: the output is drained so the child cannot block on a full pipe, and none of it is kept.
                    var stdoutTask = ProbeOutput.ReadBoundedAsync(process.StandardOutput, ProbeOutput.KeepNothing, linkedCts.Token);
                    var stderrTask = ProbeOutput.ReadBoundedAsync(process.StandardError, ProbeOutput.KeepNothing, linkedCts.Token);

                    await process.WaitForExitAsync(linkedCts.Token);
                    _ = await stdoutTask;
                    _ = await stderrTask;
                    return Classify(startFailed: false, timedOut: false, exitCode: process.ExitCode);
                }
                catch (OperationCanceledException)
                {
                    try { if (!process.HasExited) process.Kill(true); } catch { }
                    if (cancellationToken.IsCancellationRequested) throw;
                    return Classify(startFailed: false, timedOut: true, exitCode: null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return Classify(startFailed: true, timedOut: false, exitCode: null);
            }
        }
    }
}
