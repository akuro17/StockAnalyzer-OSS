using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>At-least-once notification delivery from the monitoring store's outbox table.</summary>
internal sealed class SqliteMonitoringOutbox(SqliteMonitoringStore store, TimeSpan pollInterval) : IMonitoringNotificationOutbox
{
    private const string LeaseFileName = "monitoring.outbox.lock";

    public bool IsActive => true;
    public TimeSpan PollInterval => pollInterval;

    public async Task<int> DispatchPendingAsync(Func<ModelDecayAlert, CancellationToken, Task> sink, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();
        // The process-wide dispatch lease: a second dispatcher (another process) leaves the pass to the holder.
        System.IO.Directory.CreateDirectory(store.Root);
        using var lease = SchedulerFileLease.TryAcquire(System.IO.Path.Combine(store.Root, LeaseFileName));
        if (lease is null) return 0;
        List<PendingNotification> pending;
        await using (var connection = await store.OpenAsync(requireInitialized: true, ct).ConfigureAwait(false))
            pending = await SqliteMonitoringStore.ReadPendingAsync(connection, ct).ConfigureAwait(false);
        int acknowledged = 0;
        foreach (var item in pending)
        {
            ct.ThrowIfCancellationRequested(); // cancellation leaves the remaining events pending
            try { await sink(item.Alert, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                await WriteAsync(connection => SqliteMonitoringStore.RecordFailureAsync(connection.Connection, connection.Transaction,
                    item.Alert.AlertId, item.AttemptCount, ex.Message, ct), ct).ConfigureAwait(false);
                continue;
            }
            // Acknowledged only AFTER the sink returned, in its own transaction; a crash before this repeats the toast.
            await WriteAsync(connection => SqliteMonitoringStore.AcknowledgeAsync(connection.Connection, connection.Transaction, item.Alert.AlertId, ct), ct).ConfigureAwait(false);
            acknowledged++;
        }
        return acknowledged;
    }

    private async Task WriteAsync(Func<(SqliteConnection Connection, SqliteTransaction Transaction), Task> work, CancellationToken ct)
    {
        await using var connection = await store.OpenAsync(requireInitialized: true, ct).ConfigureAwait(false);
        using var transaction = store.BeginWriter(connection, ct);
        await work((connection, transaction)).ConfigureAwait(false);
        store.Commit(transaction, ct);
    }
}
