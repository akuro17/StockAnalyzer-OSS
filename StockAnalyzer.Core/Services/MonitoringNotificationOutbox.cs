using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Durable decay-notification delivery. Delivery is AT LEAST ONCE: the sink succeeding means the toast call returned, not
/// that a user saw it, and a crash between presentation and acknowledgement may present it again after restart.
/// </summary>
public interface IMonitoringNotificationOutbox
{
    /// <summary>False for the legacy JSON backend, where decay notifications stay on the in-process <c>Decayed</c> event.</summary>
    bool IsActive { get; }

    /// <summary>Cadence of retry passes while the application runs.</summary>
    TimeSpan PollInterval { get; }

    /// <summary>
    /// One dispatch pass over the pending events, ordered by committed group revision then alert id. Each event is passed to
    /// <paramref name="sink"/> with its stable id; only after the sink returns is it acknowledged in its own transaction.
    /// A failing sink is recorded (attempt count, last error) and the event stays pending. Cancellation leaves events pending.
    /// Returns the number acknowledged; 0 when another dispatcher currently holds the store's dispatch lease.
    /// </summary>
    Task<int> DispatchPendingAsync(Func<ModelDecayAlert, CancellationToken, Task> sink, CancellationToken ct = default);
}

/// <summary>Outbox of the JSON backend: inactive, so no consumer starts a delivery loop or suppresses the legacy event.</summary>
public sealed class NoMonitoringNotificationOutbox : IMonitoringNotificationOutbox
{
    public bool IsActive => false;
    public TimeSpan PollInterval => TimeSpan.Zero;
    public Task<int> DispatchPendingAsync(Func<ModelDecayAlert, CancellationToken, Task> sink, CancellationToken ct = default) => Task.FromResult(0);
}

/// <summary>The monitoring SQLite writer could not take the database lock within the busy timeout; nothing was committed.</summary>
public sealed class PredictionMonitoringBusyException(Exception? inner = null)
    : InvalidOperationException("The prediction monitoring database is busy; the operation was not applied.", inner);

/// <summary>The monitoring database location or filesystem cannot provide the required durable SQLite configuration.</summary>
public sealed class PredictionMonitoringStorageException(string message, Exception? inner = null) : IOException(message, inner);
