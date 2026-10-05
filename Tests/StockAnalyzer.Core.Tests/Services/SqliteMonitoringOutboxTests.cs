#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>FP05: durable at-least-once delivery. The sink returning is the acknowledgement point, in its own transaction.</summary>
public sealed class SqliteMonitoringOutboxTests : IDisposable
{
    private readonly SqliteMonitoringTestKit _kit = new();
    public void Dispose() => _kit.Dispose();

    private static string Hex(string seed) => PredictionMonitoringReducer.Hash(seed);

    private async Task InsertAsync(string alertId, long? revision, string target = "target", bool presented = false)
    {
        var snapshot = new ModelMonitoringSnapshot(Hex("model"), Hex(target), 30, 14, 14m / 30, ModelHealthState.Decayed, _kit.Clock.Now, alertId);
        var store = _kit.Store();
        await using var connection = await store.OpenAsync(true, CancellationToken.None);
        using var transaction = store.BeginWriter(connection, CancellationToken.None);
        await SqliteMonitoringStore.InsertAlertAsync(connection, transaction, new ModelDecayAlert(alertId, snapshot), revision, presented, CancellationToken.None);
        store.Commit(transaction, CancellationToken.None);
    }

    private Task<(long State, long Attempts, string? Error)> RowAsync(string alertId) => _kit.QueryAsync(
        $"SELECT State, AttemptCount, LastError FROM Outbox WHERE AlertId = '{alertId}';",
        r => (r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)));

    [Fact]
    public async Task PendingEvents_AreDeliveredByRevisionThenId_AcknowledgedAfterTheSink_AndNeverRepeated()
    {
        var log = await _kit.CreateServiceAsync();
        await InsertAsync("b", 2, "t1"); await InsertAsync("z", 1, "t2"); await InsertAsync("a", 2, "t3");
        await InsertAsync("history", null, "t4", presented: true); // an imported historical alert is never delivered
        var order = new List<string>();
        Assert.Equal(3, await log.Outbox.DispatchPendingAsync((alert, _) => { order.Add(alert.AlertId); return Task.CompletedTask; }));
        Assert.Equal(["z", "a", "b"], order);
        Assert.All(new[] { "a", "b", "z" }, id => Assert.Equal(1L, RowAsync(id).Result.State));
        Assert.Equal((1L, 0L, null), await RowAsync("history"));
        Assert.Equal(0, await log.Outbox.DispatchPendingAsync((_, _) => throw new InvalidOperationException("must not run")));
        Assert.True(log.Outbox.IsActive); Assert.Equal(TimeSpan.FromMinutes(1), log.Outbox.PollInterval);
    }

    [Fact]
    public async Task FailingSink_IsRecordedWithACheckedAttemptCount_StaysPending_AndIsRetried()
    {
        var log = await _kit.CreateServiceAsync();
        await InsertAsync("bad", 1, "t1"); await InsertAsync("good", 2, "t2");
        var delivered = new List<string>();
        Assert.Equal(1, await log.Outbox.DispatchPendingAsync((alert, _) =>
        {
            if (alert.AlertId == "bad") throw new InvalidOperationException("toast surface unavailable");
            delivered.Add(alert.AlertId); return Task.CompletedTask;
        }));
        Assert.Equal((0L, 1L, "toast surface unavailable"), await RowAsync("bad"));
        Assert.Equal((1L, 0L, null), await RowAsync("good"));
        Assert.Equal(1, await log.Outbox.DispatchPendingAsync((alert, _) => { delivered.Add(alert.AlertId); return Task.CompletedTask; }));
        Assert.Equal(["good", "bad"], delivered);
        Assert.Equal((1L, 1L), ((await RowAsync("bad")).State, (await RowAsync("bad")).Attempts)); // delivered on the retry, attempt history kept

        await InsertAsync("max", 3, "t3");
        await using (var connection = await _kit.Store().OpenAsync(true, CancellationToken.None))
            await SqliteMonitoringStore.ExecAsync(connection, null, "UPDATE Outbox SET AttemptCount = 9223372036854775807 WHERE AlertId = 'max';", CancellationToken.None);
        await Assert.ThrowsAsync<OverflowException>(() => log.Outbox.DispatchPendingAsync((_, _) => throw new InvalidOperationException("again")));
        Assert.Equal((0L, long.MaxValue), ((await RowAsync("max")).State, (await RowAsync("max")).Attempts)); // never wrapped or converted to REAL
    }

    [Fact]
    public async Task CrashBetweenPresentationAndAcknowledgement_RepeatsTheToastWithTheSameStableId()
    {
        var log = await _kit.CreateServiceAsync(); await InsertAsync("stable-id", 1);
        var presentations = new List<string>(); using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.Outbox.DispatchPendingAsync((alert, _) =>
        {
            presentations.Add(alert.AlertId); cancel.Cancel(); // the "process" stops right after the toast returned
            return Task.CompletedTask;
        }, cancel.Token));
        Assert.Equal((0L, 0L), ((await RowAsync("stable-id")).State, (await RowAsync("stable-id")).Attempts)); // still pending: no ACK yet
        Assert.Equal(1, await log.Outbox.DispatchPendingAsync((alert, _) => { presentations.Add(alert.AlertId); return Task.CompletedTask; }));
        Assert.Equal(["stable-id", "stable-id"], presentations); // at-least-once: a duplicate presentation, never a lost one
        Assert.Equal(1L, (await RowAsync("stable-id")).State);
    }

    [Fact]
    public async Task CancellationDuringThePass_LeavesEventsPendingWithoutCountingAnAttempt()
    {
        var log = await _kit.CreateServiceAsync(); await InsertAsync("one", 1); await InsertAsync("two", 2, "t2");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.Outbox.DispatchPendingAsync((_, token) =>
        {
            cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask;
        }, cancel.Token));
        Assert.Equal((0L, 0L, null), await RowAsync("one"));
        Assert.Equal((0L, 0L, null), await RowAsync("two"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => log.Outbox.DispatchPendingAsync((_, _) => Task.CompletedTask, new CancellationToken(true)));
    }

    [Fact]
    public async Task HeldDispatchLease_LeavesThePassToTheCurrentHolder()
    {
        var log = await _kit.CreateServiceAsync(); await InsertAsync("held", 1);
        using (SchedulerFileLease.TryAcquire(Path.Combine(_kit.MonitorRoot, "monitoring.outbox.lock")))
            Assert.Equal(0, await log.Outbox.DispatchPendingAsync((_, _) => throw new InvalidOperationException("another dispatcher owns the lease")));
        Assert.Equal(0L, (await RowAsync("held")).State);
        Assert.Equal(1, await log.Outbox.DispatchPendingAsync((_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task TransitionCommittedByTheService_IsDeliveredOnceWithItsStableId()
    {
        await _kit.EnsureRegistryAsync(); var log = await _kit.CreateServiceAsync();
        for (int i = 0; i < 20; i++) await _kit.VoteAsync(log, "C" + i, SqliteMonitoringTestKit.Sept28, true);
        for (int i = 0; i < 10; i++) await _kit.VoteAsync(log, "W" + i, SqliteMonitoringTestKit.Sept28, true);
        Assert.Equal(ModelHealthState.Healthy, (await log.EvaluateCurrentAsync()).Single().State);
        for (int i = 0; i < 16; i++) await _kit.ObserveVoteAsync(log, i < 10 ? "W" + i : "C" + (i - 10), SqliteMonitoringTestKit.Sept28, false); // 30 votes, 14 correct
        var decayed = (await log.GetSnapshotsAsync(_kit.ModelId)).Single();
        Assert.Equal((30, 14, ModelHealthState.Decayed), (decayed.MaturedCount, decayed.CorrectCount, decayed.State));
        var seen = new List<ModelDecayAlert>();
        Assert.Equal(1, await log.Outbox.DispatchPendingAsync((alert, _) => { seen.Add(alert); return Task.CompletedTask; }));
        Assert.Equal((decayed.LastAlertId, decayed), (seen.Single().AlertId, seen.Single().Snapshot));
        Assert.Equal(0, await log.Outbox.DispatchPendingAsync((alert, _) => { seen.Add(alert); return Task.CompletedTask; }));
        Assert.Single(seen);
    }
}
