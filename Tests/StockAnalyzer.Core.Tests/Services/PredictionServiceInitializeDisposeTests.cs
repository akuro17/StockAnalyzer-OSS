using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Regression for a hang seen in full Core.Tests runs: <c>PredictAsync_ConcurrentDisposeDuringInflight_...</c> never completed. A hang dump
/// showed <c>PredictionService.InitializeAsync</c> parked on a <see cref="SemaphoreSlim"/> wait node that nothing would ever release:
/// <c>SemaphoreSlim.Dispose()</c> does not wake waiters that are already queued, and the initializer holding the lock could no longer
/// <c>Release()</c> a disposed semaphore. Here the initializer holds the lock deterministically (a registry that blocks inside
/// <c>AcquireActive</c>), a second initializer queues behind it, and the service is disposed before the first one is released.
/// </summary>
[Collection("Non-Parallel ONNX Tests")]
public class PredictionServiceInitializeDisposeTests
{
    private const int PredictionWindowSize = 10;

    /// <summary>How long the second initializer gets to reach the lock wait before the service is disposed (it can only finish early by
    /// being disposed first, which makes the test pass without proving anything, never fail).</summary>
    private static readonly TimeSpan QueueSettleTime = TimeSpan.FromMilliseconds(500);

    /// <summary>Upper bound for both initializers to finish after the disposal; a healthy run takes milliseconds.</summary>
    private static readonly TimeSpan CompletionCeiling = TimeSpan.FromSeconds(30);

    private sealed class BlockingRegistry : IModelGenerationRegistry
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Proceed { get; } = new(false);

        public string? ActiveId => "blocking-model";
        public string? PreviousId => null;
        public IReadOnlyList<ModelGenerationSummary> List() => Array.Empty<ModelGenerationSummary>();

        public Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> RollbackAsync(bool confirmLowerScore = false, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ModelGenerationLease? AcquireActive()
        {
            Entered.Set();
            Proceed.Wait();
            return null;
        }
    }

    [Fact]
    public async Task Dispose_WhileAnotherInitializeAsyncWaitsForTheLock_DoesNotLeaveTheWaiterHanging()
    {
        var registry = new BlockingRegistry();
        var service = new PredictionService(
            new PredictionServiceTests.TestSettings(PredictionServiceTests.ConformantModelPath,predictionWindowSize: PredictionWindowSize),
            new MLDataProcessor(),
            modelRegistry: registry);

        Task holder = Task.Run(() => service.InitializeAsync());
        Assert.True(registry.Entered.Wait(CompletionCeiling), "the first initializer never reached the registry");

        Task waiter = Task.Run(() => service.InitializeAsync());
        await Task.Delay(QueueSettleTime);

        service.Dispose();
        registry.Proceed.Set();

        Task all = Task.WhenAll(holder, waiter);
        Task finished = await Task.WhenAny(all, Task.Delay(CompletionCeiling));
        Assert.True(ReferenceEquals(all, finished),
            "an InitializeAsync call queued behind the lock never completed after the service was disposed");
        await all;
    }
}
