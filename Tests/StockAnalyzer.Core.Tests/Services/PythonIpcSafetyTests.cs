using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class BoundedLineReaderTests
    {
        /// <summary>A character that takes 3 bytes in UTF-8 (HIRAGANA LETTER A).</summary>
        private const char MultibyteChar = 'あ';

        private static BoundedLineReader CreateReader(string content, int maxLineBytes)
            => new(new MemoryStream(Encoding.UTF8.GetBytes(content)), maxLineBytes);

        [Fact]
        public async Task ReadLineAsync_SplitsOnLfAndKeepsSurplusForNextCall()
        {
            var reader = CreateReader("first\r\nsecond\nthird", 100);

            Assert.Equal("first", await reader.ReadLineAsync(CancellationToken.None));
            Assert.Equal("second", await reader.ReadLineAsync(CancellationToken.None));
            Assert.Equal("third", await reader.ReadLineAsync(CancellationToken.None));
            Assert.Null(await reader.ReadLineAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ReadLineAsync_LineLongerThanChunk_IsAssembled()
        {
            string longLine = new string('x', 20000);
            var reader = CreateReader(longLine + "\nnext\n", 20000);

            Assert.Equal(longLine, await reader.ReadLineAsync(CancellationToken.None));
            Assert.Equal("next", await reader.ReadLineAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ReadLineAsync_LineOverLimit_ThrowsResponseTooLarge()
        {
            var reader = CreateReader(new string('x', 101) + "\n", 100);

            await Assert.ThrowsAsync<PythonResponseTooLargeException>(async () => await reader.ReadLineAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ReadLineAsync_LimitCountsBytesNotCharacters()
        {
            // 40 x 3-byte characters (U+3042) = 120 bytes.
            var reader = CreateReader(new string(MultibyteChar, 40) + "\n", 100);

            await Assert.ThrowsAsync<PythonResponseTooLargeException>(async () => await reader.ReadLineAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ReadLineAsync_MultibyteLineWithinLimit_IsDecoded()
        {
            var reader = CreateReader(new string(MultibyteChar, 10) + "\n", 100);

            Assert.Equal(new string(MultibyteChar, 10), await reader.ReadLineAsync(CancellationToken.None));
        }
    }

    public class PythonProcessManagerRequestTimeoutTests
    {
        private sealed class TimeoutSettings : PythonIpcTestSettingsBase
        {
            public override int PythonRequestTimeoutMs => 200;
        }

        [Fact]
        public async Task WithRequestTimeoutAsync_HungOperation_ThrowsTimeoutException()
        {
            await using var manager = new PythonProcessManager(new TimeoutSettings());

            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await manager.WithRequestTimeoutAsync<int>(CancellationToken.None, async token =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return 0;
                }));
        }

        [Fact]
        public async Task WithRequestTimeoutAsync_CallerCancellation_StaysCancellation()
        {
            await using var manager = new PythonProcessManager(new TimeoutSettings());
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(20);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await manager.WithRequestTimeoutAsync<int>(cts.Token, async token =>
                {
                    await Task.Delay(Timeout.Infinite, token);
                    return 0;
                }));
        }

        [Fact]
        public async Task WithRequestTimeoutAsync_CompletedOperation_ReturnsResult()
        {
            await using var manager = new PythonProcessManager(new TimeoutSettings());

            Assert.Equal(7, await manager.WithRequestTimeoutAsync(CancellationToken.None, _ => Task.FromResult(7)));
        }
    }

    [Collection("PythonIpc")]
    public class PythonTransactionRetryTests
    {
        private sealed class RetrySettings : PythonIpcTestSettingsBase
        {
            public override string PipeName => "testtransactionretrypipe";
        }

        private sealed class TimeoutBudgetSettings : PythonIpcTestSettingsBase
        {
            private readonly int _timeoutRetries;

            public TimeoutBudgetSettings(int timeoutRetries) => _timeoutRetries = timeoutRetries;

            public override string PipeName => "testtimeoutbudgetpipe";
            public override int PythonMaxTimeoutRetries => _timeoutRetries;
        }

        [Fact]
        public async Task ExecuteTransactionAsync_TimeoutWithZeroBudget_IsNotRetried()
        {
            var pythonService = new PythonService(new TimeoutBudgetSettings(0));
            int attempts = 0;

            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await pythonService.ExecuteTransactionAsync<string>(() =>
                {
                    attempts++;
                    throw new TimeoutException("slow");
                }));

            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task ExecuteTransactionAsync_TimeoutBudgetOfOne_RerunsOnceThenSurfacesTheTimeout()
        {
            // PythonMaxRetries is 3, so without the budget this would run 4 times.
            var pythonService = new PythonService(new TimeoutBudgetSettings(1));
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();
            int attempts = 0;

            await Assert.ThrowsAsync<TimeoutException>(async () =>
                await pythonService.ExecuteTransactionAsync<string>(() =>
                {
                    attempts++;
                    throw new TimeoutException("slow");
                }));

            Assert.Equal(2, attempts);
        }

        [Fact]
        public async Task ExecuteTransactionAsync_TimeoutBudgetIsPerTransaction()
        {
            var pythonService = new PythonService(new TimeoutBudgetSettings(1));
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            for (int transaction = 0; transaction < 2; transaction++)
            {
                int attempts = 0;
                string response = await pythonService.ExecuteTransactionAsync(async () =>
                {
                    attempts++;
                    if (attempts == 1)
                    {
                        throw new TimeoutException("slow once");
                    }

                    return await pythonService.PingExternalProcessAsync();
                });

                Assert.Equal(2, attempts);
                Assert.Contains("pong", response);
            }
        }

        [Fact]
        public async Task ExecuteTransactionAsync_ConnectionLostMidTransaction_ReconnectsAndRerunsWholeTransaction()
        {
            var pythonService = new PythonService(new RetrySettings());
            await pythonService.InitializeAsync();
            await pythonService.InitializeExternalProcessAsync();

            var processManager = typeof(PythonService)
                .GetField("_processManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(pythonService)!;
            var cleanup = processManager.GetType().GetMethod("CleanupConnection", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

            int attempts = 0;
            string response = await pythonService.ExecuteTransactionAsync(async () =>
            {
                attempts++;
                if (attempts == 1)
                {
                    // What a dropped connection does in production: the pipe is torn down, then the exchange fails.
                    cleanup.Invoke(processManager, null);
                    throw new IOException("Simulated connection loss");
                }

                return await pythonService.PingExternalProcessAsync();
            });

            Assert.Equal(2, attempts);
            Assert.Contains("pong", response);
        }

        [Fact]
        public async Task ExecuteTransactionAsync_ResponseTooLarge_IsNotRetried()
        {
            var pythonService = new PythonService(new RetrySettings());
            int attempts = 0;

            await Assert.ThrowsAsync<PythonResponseTooLargeException>(async () =>
                await pythonService.ExecuteTransactionAsync<string>(() =>
                {
                    attempts++;
                    throw new PythonResponseTooLargeException(100);
                }));

            Assert.Equal(1, attempts);
        }

        [Fact]
        public async Task ExecuteTransactionAsync_NonTransientException_IsNotRetried()
        {
            var pythonService = new PythonService(new RetrySettings());
            int attempts = 0;

            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await pythonService.ExecuteTransactionAsync<string>(() =>
                {
                    attempts++;
                    throw new ArgumentException("bad input");
                }));

            Assert.Equal(1, attempts);
        }
    }
}
