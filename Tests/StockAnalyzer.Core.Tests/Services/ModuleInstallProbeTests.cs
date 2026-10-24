using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class ModuleInstallProbeTests
    {
        private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(200);

        [Fact]
        public void Classify_WhenTimedOut_ReturnsTimedOut()
        {
            Assert.Equal(ModuleInstallState.TimedOut, ModuleInstallProbe.Classify(startFailed: false, timedOut: true, exitCode: null));
        }

        [Fact]
        public void Classify_WhenStartFailed_ReturnsFailed()
        {
            Assert.Equal(ModuleInstallState.Failed, ModuleInstallProbe.Classify(startFailed: true, timedOut: false, exitCode: null));
        }

        [Fact]
        public void Classify_WhenNoExitCodeAndNoTimeout_ReturnsFailed()
        {
            Assert.Equal(ModuleInstallState.Failed, ModuleInstallProbe.Classify(startFailed: false, timedOut: false, exitCode: null));
        }

        [Fact]
        public void Classify_WhenExitCodeIsZero_ReturnsInstalled()
        {
            Assert.Equal(ModuleInstallState.Installed, ModuleInstallProbe.Classify(startFailed: false, timedOut: false, exitCode: 0));
        }

        [Fact]
        public void Classify_WhenExitCodeIsNonZero_ReturnsMissing()
        {
            Assert.Equal(ModuleInstallState.Missing, ModuleInstallProbe.Classify(startFailed: false, timedOut: false, exitCode: 1));
        }

        [Fact]
        public void IsConclusive_ForInstalledAndMissing_IsTrue()
        {
            Assert.True(ModuleInstallProbe.IsConclusive(ModuleInstallState.Installed));
            Assert.True(ModuleInstallProbe.IsConclusive(ModuleInstallState.Missing));
        }

        [Fact]
        public void IsConclusive_ForFailedAndTimedOut_IsFalse()
        {
            Assert.False(ModuleInstallProbe.IsConclusive(ModuleInstallState.Failed));
            Assert.False(ModuleInstallProbe.IsConclusive(ModuleInstallState.TimedOut));
        }

        [WindowsFact]
        public async Task RunAsync_WhenProcessExitsZero_ReturnsInstalled()
        {
            var state = await ModuleInstallProbe.RunAsync("cmd.exe", "/c exit 0", TimeSpan.FromSeconds(30));

            Assert.Equal(ModuleInstallState.Installed, state);
        }

        // L-4: a large output is drained (not kept), so the child is not blocked and the exit code decides.
        [WindowsFact]
        public async Task RunAsync_WithAVeryLongOutput_ReturnsInstalled()
        {
            var state = await ModuleInstallProbe.RunAsync("cmd.exe", "/c for /L %i in (1,1,20000) do @echo filler filler filler filler filler", TimeSpan.FromSeconds(30));

            Assert.Equal(ModuleInstallState.Installed, state);
        }

        [WindowsFact]
        public async Task RunAsync_WhenProcessExitsNonZero_ReturnsMissing()
        {
            var state = await ModuleInstallProbe.RunAsync("cmd.exe", "/c exit 1", TimeSpan.FromSeconds(30));

            Assert.Equal(ModuleInstallState.Missing, state);
        }

        [WindowsFact]
        public async Task RunAsync_WhenLimitIsExceeded_ReturnsTimedOutWithoutWaitingForProcess()
        {
            var clock = Stopwatch.StartNew();
            var state = await ModuleInstallProbe.RunAsync("cmd.exe", "/c ping -n 30 127.0.0.1 >nul", ShortLimit);
            clock.Stop();

            Assert.Equal(ModuleInstallState.TimedOut, state);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"Probe waited {clock.Elapsed} for a process that should have been stopped at {ShortLimit}.");
        }

        // N-07 A: only the internal limit is TimedOut; the caller's cancellation is not swallowed, and the child is stopped.
        [WindowsFact]
        public async Task RunAsync_WhenCallerCancels_ThrowsAtOnce()
        {
            using var cts = new CancellationTokenSource(ShortLimit);
            var clock = Stopwatch.StartNew();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ModuleInstallProbe.RunAsync("cmd.exe", "/c ping -n 30 127.0.0.1 >nul", TimeSpan.FromSeconds(30), cts.Token));

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Probe waited {clock.Elapsed} after the caller canceled.");
        }

        [WindowsFact]
        public async Task RunAsync_WhenAlreadyCanceled_Throws()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ModuleInstallProbe.RunAsync("cmd.exe", "/c exit 0", TimeSpan.FromSeconds(30), cts.Token));
        }

        [Fact]
        public async Task RunAsync_WhenExecutableDoesNotExist_ReturnsFailed()
        {
            var state = await ModuleInstallProbe.RunAsync(@"Z:\does-not-exist\python.exe", "-c \"import pandas\"", TimeSpan.FromSeconds(30));

            Assert.Equal(ModuleInstallState.Failed, state);
        }

        [WindowsFact]
        public async Task RunWithArgumentsAsync_WhenProcessExitsZero_ReturnsInstalled()
        {
            var state = await ModuleInstallProbe.RunWithArgumentsAsync("cmd.exe", new[] { "/c", "exit 0" }, TimeSpan.FromSeconds(30));

            Assert.Equal(ModuleInstallState.Installed, state);
        }

        [WindowsFact]
        public async Task RunWithArgumentsAsync_WhenProcessExitsNonZero_ReturnsMissing()
        {
            var state = await ModuleInstallProbe.RunWithArgumentsAsync("cmd.exe", new[] { "/c", "exit 1" }, TimeSpan.FromSeconds(30));

            Assert.Equal(ModuleInstallState.Missing, state);
        }

        [Fact]
        public async Task RunWithArgumentsAsync_WhenExecutableDoesNotExist_ReturnsFailed()
        {
            var state = await ModuleInstallProbe.RunWithArgumentsAsync(@"Z:\does-not-exist\python.exe", new[] { "-c", "import pandas" }, TimeSpan.FromSeconds(30));

            Assert.Equal(ModuleInstallState.Failed, state);
        }
    }
}
