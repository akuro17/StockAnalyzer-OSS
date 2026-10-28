using System;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class UpdateCheckProbeTests
    {
        private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan LongLimit = TimeSpan.FromSeconds(30);

        // A child that outlives any delay of the limit timer on a saturated thread pool (26 s seen), and the bound that separates "stopped at the
        // limit" from "ended with the child": one ping per second, so the child lives about LongLivedChildPings - 1 seconds.
        private const int LongLivedChildPings = 130;
        private static readonly TimeSpan StoppedAtTheLimitBound = TimeSpan.FromSeconds(60);
        private static readonly string LongLivedChildCommand = $"ping -n {LongLivedChildPings} 127.0.0.1 >nul";

        [Fact]
        public void Classify_UpdateAvailableOutput_ReturnsUpdateAvailable()
        {
            var result = UpdateCheckProbe.Classify(startFailed: false, timedOut: false, exitCode: 0, output: "UPDATE_AVAILABLE\r\n");
            Assert.Equal(UpdateCheckState.UpdateAvailable, result.State);
        }

        [Fact]
        public void Classify_NoUpdateOutput_ReturnsUpToDate()
        {
            var result = UpdateCheckProbe.Classify(startFailed: false, timedOut: false, exitCode: 0, output: "NO_UPDATE\n");
            Assert.Equal(UpdateCheckState.UpToDate, result.State);
        }

        [Fact]
        public void Classify_ErrorOutput_ReturnsCheckFailedWithTheMessage()
        {
            var result = UpdateCheckProbe.Classify(startFailed: false, timedOut: false, exitCode: 0, output: "ERROR: <urlopen error timed out>\n");
            Assert.Equal(UpdateCheckState.CheckFailed, result.State);
            Assert.Contains("urlopen error timed out", result.Reason);
        }

        [Theory]
        [InlineData("")]
        [InlineData("something else")]
        public void Classify_UnknownOutput_ReturnsCheckFailed(string output)
        {
            var result = UpdateCheckProbe.Classify(startFailed: false, timedOut: false, exitCode: 0, output: output);
            Assert.Equal(UpdateCheckState.CheckFailed, result.State);
            Assert.False(string.IsNullOrWhiteSpace(result.Reason));
        }

        [Fact]
        public void Classify_NonZeroExit_ReturnsCheckFailedEvenWithUpdateOutput()
        {
            var result = UpdateCheckProbe.Classify(startFailed: false, timedOut: false, exitCode: 1, output: "UPDATE_AVAILABLE");
            Assert.Equal(UpdateCheckState.CheckFailed, result.State);
            Assert.Contains("1", result.Reason);
        }

        [Fact]
        public void Classify_TimedOut_ReturnsCheckFailed()
        {
            var result = UpdateCheckProbe.Classify(startFailed: false, timedOut: true, exitCode: null, output: null);
            Assert.Equal(UpdateCheckState.CheckFailed, result.State);
        }

        [Fact]
        public void Classify_StartFailed_ReturnsCheckFailed()
        {
            var result = UpdateCheckProbe.Classify(startFailed: true, timedOut: false, exitCode: null, output: null);
            Assert.Equal(UpdateCheckState.CheckFailed, result.State);
        }

        [WindowsFact]
        public async Task RunAsync_OutputOfTheProcess_IsClassified()
        {
            // "more" prints what it reads from standard input.
            var result = await UpdateCheckProbe.RunAsync("cmd.exe", new[] { "/c", "more" }, "UPDATE_AVAILABLE", LongLimit, CancellationToken.None);
            Assert.Equal(UpdateCheckState.UpdateAvailable, result.State);
        }

        [WindowsFact]
        public async Task RunAsync_NonZeroExit_ReturnsCheckFailed()
        {
            var result = await UpdateCheckProbe.RunAsync("cmd.exe", new[] { "/c", "more >nul & exit /b 3" }, "NO_UPDATE", LongLimit, CancellationToken.None);
            Assert.Equal(UpdateCheckState.CheckFailed, result.State);
        }

        [WindowsFact]
        public async Task RunAsync_WhenLimitIsExceeded_ReturnsCheckFailedQuickly()
        {
            var started = DateTime.UtcNow;
            var result = await UpdateCheckProbe.RunAsync("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, "x", ShortLimit, CancellationToken.None);

            Assert.Equal(UpdateCheckState.CheckFailed, result.State);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "the process must be stopped when the limit is exceeded");
        }

        // N-07 A: only the internal limit is a failed check; the caller's cancellation is not swallowed.
        [WindowsFact]
        public async Task RunAsync_WhenCallerCancels_Throws()
        {
            using var cts = new CancellationTokenSource(ShortLimit);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                UpdateCheckProbe.RunAsync("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, "x", LongLimit, cts.Token));
        }

        // L-4: a child that never reads its input must not hold the check past the limit (the input is larger than the pipe buffer).
        // The limit timer runs on the thread pool, so a pool saturated by parallel tests delays it (26 s seen in the full suite). The child
        // therefore lives far longer than that delay and the bound sits between the two: a write bound by the limit returns whenever the timer
        // finally runs, a write bound by the child's life would take about as long as the child lives.
        [WindowsFact]
        public async Task RunAsync_WhenTheChildDoesNotReadItsInput_ReturnsCheckFailedAtTheLimit()
        {
            var started = DateTime.UtcNow;
            var result = await UpdateCheckProbe.RunAsync("cmd.exe", new[] { "/c", LongLivedChildCommand }, new string('x', 4_000_000), ShortLimit, CancellationToken.None);

            Assert.Equal(UpdateCheckState.CheckFailed, result.State);
            Assert.True(DateTime.UtcNow - started < StoppedAtTheLimitBound, "the input write must be bound by the limit");
        }

        [WindowsFact]
        public async Task RunAsync_WhenTheChildDoesNotReadItsInput_CallerCancellationIsRethrown()
        {
            using var cts = new CancellationTokenSource(ShortLimit);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                UpdateCheckProbe.RunAsync("cmd.exe", new[] { "/c", "ping -n 30 127.0.0.1 >nul" }, new string('x', 4_000_000), LongLimit, cts.Token));
        }

        // L-4: a large output is read to its end (the child is not blocked) and the marker at its start still decides.
        [WindowsFact]
        public async Task RunAsync_WithAVeryLongOutput_StillClassifiesTheMarker()
        {
            var result = await UpdateCheckProbe.RunAsync(
                "cmd.exe", new[] { "/c", "echo UPDATE_AVAILABLE & for /L %i in (1,1,20000) do @echo filler filler filler filler filler" }, "x", LongLimit, CancellationToken.None);

            Assert.Equal(UpdateCheckState.UpdateAvailable, result.State);
        }

        [Fact]
        public async Task RunAsync_WhenProcessCannotStart_ReturnsCheckFailed()
        {
            var result = await UpdateCheckProbe.RunAsync(@"Z:\no\such\interpreter.exe", Array.Empty<string>(), "x", LongLimit, CancellationToken.None);
            Assert.Equal(UpdateCheckState.CheckFailed, result.State);
        }
    }
}
