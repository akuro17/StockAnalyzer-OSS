using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services
{
    public class PipSecurityGateTests
    {
        [Fact]
        public async Task EnsureAsync_WhenInstalledPipIsAtMinimum_DoesNotUpgrade()
        {
            var gate = new PipSecurityGate();
            int upgrades = 0;

            await gate.EnsureAsync(
                _ => Task.FromResult<string?>(PythonService.MinimumPipVersion),
                _ => { upgrades++; return Task.CompletedTask; },
                CancellationToken.None);

            Assert.Equal(0, upgrades);
        }

        [Fact]
        public async Task EnsureAsync_WhenInstalledPipIsOlder_UpgradesOnce()
        {
            var gate = new PipSecurityGate();
            int upgrades = 0;

            await gate.EnsureAsync(
                _ => Task.FromResult<string?>("26.1.0"),
                _ => { upgrades++; return Task.CompletedTask; },
                CancellationToken.None);

            Assert.Equal(1, upgrades);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("not-a-version")]
        public async Task EnsureAsync_WhenVersionCannotBeRead_Upgrades(string? installed)
        {
            var gate = new PipSecurityGate();
            int upgrades = 0;

            await gate.EnsureAsync(
                _ => Task.FromResult(installed),
                _ => { upgrades++; return Task.CompletedTask; },
                CancellationToken.None);

            Assert.Equal(1, upgrades);
        }

        [Fact]
        public async Task EnsureAsync_AfterSuccess_DoesNotReadVersionAgain()
        {
            var gate = new PipSecurityGate();
            int reads = 0;

            await gate.EnsureAsync(
                _ => { reads++; return Task.FromResult<string?>(PythonService.MinimumPipVersion); },
                _ => Task.CompletedTask,
                CancellationToken.None);
            await gate.EnsureAsync(
                _ => { reads++; return Task.FromResult<string?>(PythonService.MinimumPipVersion); },
                _ => Task.CompletedTask,
                CancellationToken.None);

            Assert.Equal(1, reads);
        }

        [Fact]
        public async Task EnsureAsync_ConcurrentCalls_ReadAndUpgradeOnlyOnce()
        {
            var gate = new PipSecurityGate();
            int reads = 0;
            int upgrades = 0;

            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => gate.EnsureAsync(
                async _ => { Interlocked.Increment(ref reads); await Task.Delay(5); return "26.1.0"; },
                async _ => { Interlocked.Increment(ref upgrades); await Task.Delay(20); },
                CancellationToken.None)));

            Assert.Equal(1, reads);
            Assert.Equal(1, upgrades);
        }

        [Fact]
        public async Task EnsureAsync_WhenUpgradeFails_ThrowsAndRetriesOnNextCall()
        {
            var gate = new PipSecurityGate();
            int upgrades = 0;

            await Assert.ThrowsAsync<InvalidOperationException>(() => gate.EnsureAsync(
                _ => Task.FromResult<string?>("26.1.0"),
                _ => { upgrades++; throw new InvalidOperationException("network down"); },
                CancellationToken.None));

            await gate.EnsureAsync(
                _ => Task.FromResult<string?>("26.1.0"),
                _ => { upgrades++; return Task.CompletedTask; },
                CancellationToken.None);

            Assert.Equal(2, upgrades);
        }
    }
}
