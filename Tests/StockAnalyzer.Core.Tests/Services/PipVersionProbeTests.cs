using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class PipVersionProbeTests
    {
        private static readonly TimeSpan ShortLimit = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan LongLimit = TimeSpan.FromSeconds(30);

        [WindowsFact]
        public async Task ReadAsync_ReturnsTheTrimmedOutput()
        {
            var version = await PipVersionProbe.ReadAsync("cmd.exe", "/c echo 26.2.2", LongLimit, CancellationToken.None);

            Assert.Equal("26.2.2", version);
        }

        [WindowsFact]
        public async Task ReadAsync_WhenTheProcessFails_ReturnsNull()
        {
            Assert.Null(await PipVersionProbe.ReadAsync("cmd.exe", "/c echo 26.2.2 & exit /b 1", LongLimit, CancellationToken.None));
        }

        [WindowsFact]
        public async Task ReadAsync_WhenThereIsNoOutput_ReturnsNull()
        {
            Assert.Null(await PipVersionProbe.ReadAsync("cmd.exe", "/c exit 0", LongLimit, CancellationToken.None));
        }

        // R-4: a hung interpreter ends as "cannot be read" at the limit, and the child is stopped.
        [WindowsFact]
        public async Task ReadAsync_WhenTheLimitIsExceeded_ReturnsNullQuickly()
        {
            var clock = Stopwatch.StartNew();
            var version = await PipVersionProbe.ReadAsync("cmd.exe", "/c ping -n 30 127.0.0.1 >nul", ShortLimit, CancellationToken.None);

            Assert.Null(version);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Probe waited {clock.Elapsed} for a process that should have been stopped at {ShortLimit}.");
        }

        // N-07 A: only the internal limit is "cannot be read"; the caller's cancellation is not swallowed.
        [WindowsFact]
        public async Task ReadAsync_WhenCallerCancels_Throws()
        {
            using var cts = new CancellationTokenSource(ShortLimit);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                PipVersionProbe.ReadAsync("cmd.exe", "/c ping -n 30 127.0.0.1 >nul", LongLimit, cts.Token));
        }

        // R-4: the text kept is bounded, and a large output does not block the child.
        [WindowsFact]
        public async Task ReadAsync_WithAVeryLongOutput_KeepsOnlyTheBoundedStart()
        {
            var version = await PipVersionProbe.ReadAsync(
                "cmd.exe", "/c for /L %i in (1,1,20000) do @echo 26.2.2", LongLimit, CancellationToken.None);

            Assert.NotNull(version);
            Assert.True(version!.Length <= ProbeOutputLimits.PipVersionKeptChars, $"kept {version.Length} characters");
        }

        [Fact]
        public async Task ReadAsync_WhenProcessCannotStart_Throws()
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                PipVersionProbe.ReadAsync(@"Z:\no\such\python.exe", string.Empty, LongLimit, CancellationToken.None));
        }

        // The kept text must still hold what the checks read.
        [Fact]
        public void Limits_HoldWhatTheChecksRead()
        {
            Assert.True(ProbeOutputLimits.PipVersionKeptChars >= "1000.1000.1000.post1000".Length);
            Assert.True(ProbeOutputLimits.UpdateCheckKeptChars >= UpdateCheckProbe.ErrorMarker.Length + 512);
            Assert.True(ProbeOutputLimits.ReadChunkChars > 0);
        }
    }
}
