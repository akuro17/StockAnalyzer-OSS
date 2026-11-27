using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    [Collection("ChildProcess")]
    public class SubprocessDeadlockSafetyTests
    {
        [Fact]
        public async Task RunSubprocessWithConcurrentDrain_LargeOutput_CompletesWithoutDeadlock()
        {
            // Verify that draining stdout and stderr concurrently before WaitForExitAsync
            // completes without blocking on OS pipe buffer limits (4KB-64KB).
            var isWindows = OperatingSystem.IsWindows();
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = isWindows ? "cmd.exe" : "sh",
                Arguments = isWindows
                    ? "/c \"for /L %i in (1,1,2000) do @echo Line %i: 0123456789ABCDEF0123456789ABCDEF\""
                    : "-c \"for i in $(seq 1 2000); do echo Line $i: 0123456789ABCDEF0123456789ABCDEF; done\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = new System.Diagnostics.Process { StartInfo = startInfo };
            Assert.True(process.Start());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            Assert.Equal(0, process.ExitCode);
            Assert.True(stdout.Length > 65536, $"Expected output length > 64KB to exceed typical pipe buffer, got {stdout.Length}");
        }
    }
}
