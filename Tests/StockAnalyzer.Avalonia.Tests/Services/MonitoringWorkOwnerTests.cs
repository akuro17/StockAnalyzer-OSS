using StockAnalyzer.Avalonia.Services;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Services;

/// <summary>FP07: one owner registers, cancels and drains every monitoring task; no early release and no timeout.</summary>
public sealed class MonitoringWorkOwnerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task StopAsync_WaitsForRegisteredWork_CancelsItsToken_AndEveryCallSharesOneCompletion()
    {
        var owner = new MonitoringWorkOwner(); var entered = new TaskCompletionSource<CancellationToken>(); var exit = Gate();
        var work = owner.Run(async ct => { entered.SetResult(ct); await exit.Task; }, CancellationToken.None);
        var token = await entered.Task.WaitAsync(Limit);

        var stop = owner.StopAsync();
        Assert.True(token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);                 // the worker has not finished: nothing is released early
        Assert.Same(stop, owner.StopAsync());
        exit.SetResult();
        await stop.WaitAsync(Limit); await work.WaitAsync(Limit);
        Assert.Same(stop, owner.StopAsync());
    }

    [Fact]
    public async Task Run_WhileStoppingOrAfterStop_IsRejectedBeforeAnyWorkStarts()
    {
        var owner = new MonitoringWorkOwner(); var entered = Gate(); var exit = Gate(); int started = 0;
        var running = owner.Run(async _ => { entered.SetResult(); await exit.Task; }, CancellationToken.None);
        await entered.Task.WaitAsync(Limit);
        var stop = owner.StopAsync();                    // Stopping: the worker is still alive

        var whileStopping = owner.Run(_ => { Interlocked.Increment(ref started); return Task.CompletedTask; }, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => whileStopping);
        Assert.True(whileStopping.IsCanceled);

        exit.SetResult(); await stop.WaitAsync(Limit); await running.WaitAsync(Limit);
        var afterStop = owner.Run(_ => { Interlocked.Increment(ref started); return Task.CompletedTask; }, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => afterStop);
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task Run_WithAnAlreadyCanceledToken_NeverStartsTheWork()
    {
        var owner = new MonitoringWorkOwner(); int started = 0;
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var work = owner.Run(_ => { Interlocked.Increment(ref started); return Task.CompletedTask; }, canceled.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        await owner.StopAsync().WaitAsync(Limit);
        Assert.Equal(0, started);
    }

    [Fact]
    public async Task CallerCancellation_CancelsOnlyThatWork()
    {
        var owner = new MonitoringWorkOwner(); using var callerA = new CancellationTokenSource();
        var enteredA = new TaskCompletionSource<CancellationToken>(); var enteredB = new TaskCompletionSource<CancellationToken>();
        var workA = owner.Run(async ct => { enteredA.SetResult(ct); await Task.Delay(Timeout.Infinite, ct); }, callerA.Token);
        var exitB = Gate();
        var workB = owner.Run(async ct => { enteredB.SetResult(ct); await exitB.Task; }, CancellationToken.None);
        var tokenA = await enteredA.Task.WaitAsync(Limit); var tokenB = await enteredB.Task.WaitAsync(Limit);

        callerA.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workA.WaitAsync(Limit));
        Assert.True(tokenA.IsCancellationRequested); Assert.False(tokenB.IsCancellationRequested);
        exitB.SetResult(); await workB.WaitAsync(Limit);
        await owner.StopAsync().WaitAsync(Limit);
    }

    [Fact]
    public async Task WorkObservingTheStopToken_EndsAndTheDrainCompletes()
    {
        var owner = new MonitoringWorkOwner(); var entered = Gate();
        var work = owner.Run(async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }, CancellationToken.None);
        await entered.Task.WaitAsync(Limit);
        await owner.StopAsync().WaitAsync(Limit);
        Assert.True(work.IsCanceled);
    }

    [Fact]
    public async Task FaultingWork_FaultsItsOwnTask_AndNeverBlocksOrBreaksTheDrain()
    {
        var owner = new MonitoringWorkOwner();
        var work = owner.Run(_ => throw new InvalidOperationException("boom"), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.WaitAsync(Limit));
        var other = owner.Run(_ => Task.FromException(new IOException("later")), CancellationToken.None);
        await owner.StopAsync().WaitAsync(Limit);
        Assert.True(other.IsCompleted);
    }

    [Theory]
    [InlineData(false)] // work that faults at once
    [InlineData(true)]  // work that completes normally
    public async Task StopAsync_CompletesOnlyAfterEveryTaskReturnedByRunHasCompleted(bool succeeds)
    {
        // The registry must track the task handed to the caller, not an earlier internal signal: otherwise Stop can finish while
        // the caller's task is still completing. The window is a few instructions wide, so many parallel rounds are needed to hit it.
        const int rounds = 4000;
        var incomplete = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, rounds), async (_, _) =>
        {
            var owner = new MonitoringWorkOwner();
            var work = owner.Run(_ => succeeds ? Task.CompletedTask : Task.FromException(new IOException("later")), CancellationToken.None);
            await owner.StopAsync().WaitAsync(Limit);
            if (!work.IsCompleted) Interlocked.Increment(ref incomplete);
            await work.ContinueWith(_ => { }).WaitAsync(Limit);
        });
        Assert.Equal(0, incomplete);
    }

    [Fact]
    public async Task CompletedWork_IsUnregistered_SoStopDoesNotWaitForIt()
    {
        var owner = new MonitoringWorkOwner();
        await owner.Run(_ => Task.CompletedTask, CancellationToken.None);
        await owner.StopAsync().WaitAsync(Limit);
    }

    [Fact]
    public async Task WorkFinishingWhileStopSnapshotsTheRegistry_IsNeverLostOrWaitedForForever()
    {
        for (int round = 0; round < 60; round++)
        {
            var owner = new MonitoringWorkOwner(); int finished = 0;
            var gates = Enumerable.Range(0, 40).Select(_ => Gate()).ToArray();
            var works = gates.Select(g => owner.Run(async _ => { await g.Task; Interlocked.Increment(ref finished); }, CancellationToken.None)).ToArray();
            var releaser = Task.Run(() => { foreach (var g in gates) g.TrySetResult(); });   // completes while Stop captures the registry
            await owner.StopAsync().WaitAsync(Limit); await releaser;
            // Work that had not started when Stop won the race is canceled before it runs; everything else ran to completion.
            await Task.WhenAll(works.Select(w => w.ContinueWith(_ => { }))).WaitAsync(Limit);
            Assert.All(works, w => Assert.True(w.IsCompleted));
            Assert.Equal(works.Count(w => w.IsCompletedSuccessfully), finished);
            Assert.All(works, w => Assert.True(w.IsCompletedSuccessfully || w.IsCanceled));
        }
    }

    // ---- state machine (Running -> Stopping -> Stopped): entry races, repeated stops, rejected work, exception paths ----

    [Fact]
    public async Task RunAndStopRacingFromManyThreads_NeverLeaveStartedWorkRunning_AndEveryTaskEndsAsSuccessOrCanceled()
    {
        for (int round = 0; round < 40; round++)
        {
            var owner = new MonitoringWorkOwner(); var tasks = new System.Collections.Concurrent.ConcurrentBag<Task>();
            int started = 0, running = 0; using var go = new Barrier(9);
            var producers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                go.SignalAndWait();
                for (int i = 0; i < 50; i++)
                    tasks.Add(owner.Run(async _ =>
                    {
                        Interlocked.Increment(ref started); Interlocked.Increment(ref running);
                        try { await Task.Yield(); } finally { Interlocked.Decrement(ref running); }
                    }, CancellationToken.None));
            })).ToArray();
            var stopper = Task.Run<Task>(() => { go.SignalAndWait(); return owner.StopAsync(); });

            await (await stopper).WaitAsync(Limit);
            // Stop completed: every worker that ever started has finished, and none can start afterwards.
            Assert.Equal(0, Volatile.Read(ref running));
            var startedAtStop = Volatile.Read(ref started);
            await Task.WhenAll(producers).WaitAsync(Limit);
            await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }))).WaitAsync(Limit);
            Assert.Equal(startedAtStop, Volatile.Read(ref started));
            Assert.All(tasks, t => Assert.True(t.IsCompletedSuccessfully || t.IsCanceled));
            Assert.Equal(started, tasks.Count(t => t.IsCompletedSuccessfully));
            Assert.Equal(400, tasks.Count);
        }
    }

    [Fact]
    public async Task ConcurrentStopCalls_AllReturnTheSameCompletion_AndAnEmptyOwnerStopsAtOnce()
    {
        var owner = new MonitoringWorkOwner(); var exit = Gate(); var entered = Gate();
        var work = owner.Run(async _ => { entered.SetResult(); await exit.Task; }, CancellationToken.None);
        await entered.Task.WaitAsync(Limit);
        var stops = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run<Task>(() => owner.StopAsync())));
        Assert.All(stops, s => Assert.Same(stops[0], s));
        Assert.False(stops[0].IsCompleted);
        exit.SetResult(); await stops[0].WaitAsync(Limit); await work.WaitAsync(Limit);

        var empty = new MonitoringWorkOwner();
        var stop = empty.StopAsync();
        await stop.WaitAsync(Limit);
        Assert.Same(stop, empty.StopAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => empty.Run(_ => Task.CompletedTask, CancellationToken.None));
    }

    [Fact]
    public async Task WorkFaultingAfterAnAwait_FaultsOnlyItsOwnTask_WhileStopCompletesSuccessfully()
    {
        var owner = new MonitoringWorkOwner(); var entered = Gate(); var release = Gate();
        var faulting = owner.Run(async _ => { entered.SetResult(); await release.Task; throw new IOException("late"); }, CancellationToken.None);
        await entered.Task.WaitAsync(Limit);
        var stop = owner.StopAsync();
        release.SetResult();
        await stop.WaitAsync(Limit);
        Assert.True(stop.IsCompletedSuccessfully);                       // a worker's fault never becomes the shutdown's fault
        var error = await Assert.ThrowsAsync<IOException>(() => faulting.WaitAsync(Limit));
        Assert.Equal("late", error.Message);
    }

    [Fact]
    public async Task WorkStartedBeforeStop_CannotRegisterMoreWorkOnceStopHasBegun()
    {
        var owner = new MonitoringWorkOwner(); var entered = Gate(); int nestedStarted = 0; Task? nested = null;
        var work = owner.Run(async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            nested = owner.Run(_ => { Interlocked.Increment(ref nestedStarted); return Task.CompletedTask; }, CancellationToken.None);
        }, CancellationToken.None);
        await entered.Task.WaitAsync(Limit);
        await owner.StopAsync().WaitAsync(Limit);
        await work.WaitAsync(Limit);
        Assert.NotNull(nested);
        Assert.True(nested!.IsCanceled);
        Assert.Equal(0, nestedStarted);
    }

    [Fact]
    public async Task ACancellationCallbackThatThrows_DoesNotStopTheDrain()
    {
        var owner = new MonitoringWorkOwner(); var entered = Gate(); var exit = Gate();
        var work = owner.Run(async ct =>
        {
            using var registration = ct.Register(() => throw new InvalidOperationException("callback"));
            entered.SetResult();
            await exit.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(Limit);
        var stop = owner.StopAsync();
        exit.SetResult();
        await stop.WaitAsync(Limit);
        await work.WaitAsync(Limit);
        Assert.True(stop.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CallerCancellationRacingStop_NeverFaultsTheDrain_AndEveryTaskCompletes()
    {
        for (int round = 0; round < 40; round++)
        {
            var owner = new MonitoringWorkOwner(); using var caller = new CancellationTokenSource();
            var works = Enumerable.Range(0, 20).Select(i => owner.Run(ct => Task.Delay(Timeout.Infinite, ct),
                i % 2 == 0 ? caller.Token : CancellationToken.None)).ToArray();
            var cancel = Task.Run(() => caller.Cancel());
            var stop = owner.StopAsync();
            await Task.WhenAll(cancel, stop).WaitAsync(Limit);
            Assert.True(stop.IsCompletedSuccessfully);
            await Task.WhenAll(works.Select(w => w.ContinueWith(_ => { }))).WaitAsync(Limit);
            Assert.All(works, w => Assert.True(w.IsCanceled));
        }
    }

    [Fact]
    public void Run_RejectsAMissingWorkDelegate() =>
        Assert.Throws<ArgumentNullException>(() => { _ = new MonitoringWorkOwner().Run(null!, CancellationToken.None); });
}
