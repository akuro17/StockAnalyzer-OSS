using System;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Models.UI;
using Xunit;

namespace StockAnalyzer.Tests.Services
{
    public class LayoutSaveSchedulerTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(60001)]
        public void BoundaryValidation_ThrowsExceptionOnInvalidThrottle(int invalidThrottle)
        {
            var store = new LayoutStateStore();
            Assert.Throws<ArgumentOutOfRangeException>(() => new LayoutSaveScheduler(store, null, invalidThrottle));
        }

        [Fact]
        public void SingleRegistration_ThrowsExceptionOnSecondRegistration()
        {
            var store = new LayoutStateStore();
            var scheduler = new LayoutSaveScheduler(store, null, 100);
            scheduler.RegisterSaveAction(() => Task.CompletedTask);

            Assert.Throws<InvalidOperationException>(() => scheduler.RegisterSaveAction(() => Task.CompletedTask));
        }

        [Fact]
        public async Task Debouncing_MergesMultipleRequestsWithinThrottleWindow()
        {
            var store = new LayoutStateStore();
            store.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;
            store.LifecycleState = WorkspaceLifecycleState.Ready;
            var scheduler = new LayoutSaveScheduler(store, null, 100); // 100ms throttle

            int saveCount = 0;
            scheduler.RegisterSaveAction(() =>
            {
                Interlocked.Increment(ref saveCount);
                return Task.CompletedTask;
            });

            scheduler.RequestSave(LayoutChangeReason.PanelResized);
            scheduler.RequestSave(LayoutChangeReason.TabMoved);
            scheduler.RequestSave(LayoutChangeReason.SelectionChanged);

            // Wait long enough for throttle (100ms) + buffer
            await Task.Delay(250);

            Assert.Equal(1, saveCount);
        }

        [Fact]
        public async Task LifecycleSafety_IgnoresSavesWhenNotReady()
        {
            var store = new LayoutStateStore(); // Default state is Loading/Initialized
            var scheduler = new LayoutSaveScheduler(store, null, 50);

            int saveCount = 0;
            scheduler.RegisterSaveAction(() =>
            {
                Interlocked.Increment(ref saveCount);
                return Task.CompletedTask;
            });

            scheduler.RequestSave(LayoutChangeReason.PanelResized);

            await Task.Delay(150);

            Assert.Equal(0, saveCount);
        }

        [Fact]
        public async Task LifecycleSafety_ShutdownAllowedEvenWhenNotReady()
        {
            var store = new LayoutStateStore(); // Default state is Loading/Initialized
            var scheduler = new LayoutSaveScheduler(store, null, 50);

            int saveCount = 0;
            scheduler.RegisterSaveAction(() =>
            {
                Interlocked.Increment(ref saveCount);
                return Task.CompletedTask;
            });

            scheduler.RequestSave(LayoutChangeReason.Shutdown); // Shutdown bypasses state checks

            await Task.Delay(150);

            Assert.Equal(1, saveCount);
        }

        [Fact]
        public async Task Serialization_ForceSaveImmediateIsSerializedSequentially()
        {
            var store = new LayoutStateStore();
            var scheduler = new LayoutSaveScheduler(store, null, 50);

            int concurrentCount = 0;
            int maxConcurrentCount = 0;
            var lockObj = new object();

            scheduler.RegisterSaveAction(async () =>
            {
                int current = Interlocked.Increment(ref concurrentCount);
                lock (lockObj)
                {
                    if (current > maxConcurrentCount)
                        maxConcurrentCount = current;
                }

                await Task.Delay(30); // Simulate asynchronous disk write

                Interlocked.Decrement(ref concurrentCount);
            });

            // Run multiple immediate force-saves concurrently in different tasks
            var task1 = Task.Run(() => scheduler.ForceSaveImmediateAsync());
            var task2 = Task.Run(() => scheduler.ForceSaveImmediateAsync());
            var task3 = Task.Run(() => scheduler.ForceSaveImmediateAsync());

            await Task.WhenAll(task1, task2, task3);

            // Max concurrent count should be exactly 1 due to SemaphoreSlim serialization
            Assert.Equal(1, maxConcurrentCount);
        }

        [Fact]
        public async Task CleanDisposal_CancelsPendingTimers()
        {
            var store = new LayoutStateStore();
            store.LifecycleState = WorkspaceLifecycleState.LoadingWorkspace;
            store.LifecycleState = WorkspaceLifecycleState.Ready;
            var scheduler = new LayoutSaveScheduler(store, null, 100);

            int saveCount = 0;
            scheduler.RegisterSaveAction(() =>
            {
                Interlocked.Increment(ref saveCount);
                return Task.CompletedTask;
            });

            scheduler.RequestSave(LayoutChangeReason.PanelResized);

            // Dispose immediately during debouncing
            await scheduler.DisposeAsync();

            // Wait long enough for throttle + buffer
            await Task.Delay(200);

            Assert.Equal(0, saveCount);
        }
    }
}
