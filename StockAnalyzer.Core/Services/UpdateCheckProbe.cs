using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services
{
    /// <summary>Outcome of the update check of one package.</summary>
    internal enum UpdateCheckState
    {
        /// <summary>The check ran and found no newer release.</summary>
        UpToDate,

        /// <summary>The check ran and found a newer release.</summary>
        UpdateAvailable,

        /// <summary>The check could not tell (timeout, process or network error, unexpected output). Says nothing about whether a newer release exists.</summary>
        CheckFailed
    }

    /// <summary>State of an update check and, for <see cref="UpdateCheckState.CheckFailed"/>, the reason (for the log only).</summary>
    internal readonly record struct UpdateCheckResult(UpdateCheckState State, string? Reason = null);

    /// <summary>
    /// Runs the update-check script and classifies the outcome. A failed check is reported as such (not as "no update") so the log shows a
    /// broken check; the install step still updates only on <see cref="UpdateCheckState.UpdateAvailable"/>.
    /// </summary>
    internal static class UpdateCheckProbe
    {
        /// <summary>First word the script prints when a newer release exists.</summary>
        public const string UpdateAvailableMarker = "UPDATE_AVAILABLE";

        /// <summary>Word the script prints when there is no newer release.</summary>
        public const string NoUpdateMarker = "NO_UPDATE";

        /// <summary>Prefix of the line the script prints when it fails.</summary>
        public const string ErrorMarker = "ERROR:";

        /// <summary>Maps the outcome of a finished or aborted check to its result.</summary>
        public static UpdateCheckResult Classify(bool startFailed, bool timedOut, int? exitCode, string? output)
        {
            if (timedOut) return Failed("the check did not finish within the time limit");
            if (startFailed || exitCode is null) return Failed("the check process could not be run");
            if (exitCode != 0) return Failed($"the check process ended with exit code {exitCode}");

            var text = output ?? string.Empty;
            if (text.Contains(UpdateAvailableMarker, StringComparison.Ordinal)) return new UpdateCheckResult(UpdateCheckState.UpdateAvailable);
            if (text.Contains(NoUpdateMarker, StringComparison.Ordinal)) return new UpdateCheckResult(UpdateCheckState.UpToDate);

            var firstLine = FirstLine(text);
            return Failed(firstLine.StartsWith(ErrorMarker, StringComparison.Ordinal)
                ? firstLine[ErrorMarker.Length..].Trim()
                : $"unexpected output '{firstLine}'");
        }

        /// <summary>
        /// Starts the process, writes <paramref name="standardInput"/> to it, waits up to <paramref name="timeout"/> and classifies the result.
        /// The process tree is killed on timeout or cancellation. Only the internal timeout is a failed check; cancellation through
        /// <paramref name="cancellationToken"/> is rethrown.
        /// </summary>
        public static async Task<UpdateCheckResult> RunAsync(string fileName, string[] arguments, string standardInput, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

                using var process = Process.Start(startInfo);
                if (process == null) return Classify(startFailed: true, timedOut: false, exitCode: null, output: null);

                // The limit starts with the process and covers the input write too: a child that never reads its input must not hold the check.
                using var cts = new CancellationTokenSource(timeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token);
                try
                {
                    // The readers run before the write so a child that prints while it is still reading cannot block on a full pipe.
                    var stdoutTask = ProbeOutput.ReadBoundedAsync(process.StandardOutput, ProbeOutputLimits.UpdateCheckKeptChars, linkedCts.Token);
                    var stderrTask = ProbeOutput.ReadBoundedAsync(process.StandardError, ProbeOutput.KeepNothing, linkedCts.Token);

                    await process.StandardInput.WriteAsync(standardInput.AsMemory(), linkedCts.Token);
                    process.StandardInput.Close();

                    await process.WaitForExitAsync(linkedCts.Token);
                    var output = await stdoutTask;
                    _ = await stderrTask;
                    return Classify(startFailed: false, timedOut: false, exitCode: process.ExitCode, output);
                }
                catch (OperationCanceledException)
                {
                    StopProcess(process);
                    if (cancellationToken.IsCancellationRequested) throw;
                    return Classify(startFailed: false, timedOut: true, exitCode: null, output: null);
                }
                catch
                {
                    // Not the limit and not the caller (for example the child closed its input early): stop the child, report the failure below.
                    StopProcess(process);
                    throw;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Failed($"the check process failed: {ex.Message}");
            }
        }

        private static void StopProcess(Process process)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        }

        private static UpdateCheckResult Failed(string reason) => new(UpdateCheckState.CheckFailed, reason);

        private static string FirstLine(string text)
        {
            var trimmed = text.Trim();
            var end = trimmed.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? trimmed : trimmed[..end];
        }
    }
}
