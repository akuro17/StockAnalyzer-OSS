using System;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StockAnalyzer.Core.Common;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Bounded pool of independent in-memory <see cref="DuckDBConnection"/> instances initialized with <c>SET threads=1;</c>.
/// Eliminates contention between parallel chart/screener reads and background <see cref="DuckDBConnectionManager"/> work while
/// preventing oversubscription inside DuckDB.
/// </summary>
public sealed class ReadOnlyDuckDbConnectionPool : IReadOnlyParquetConnectionPool
{
    public const int MinDefaultPoolSize = 2;
    public const int MaxDefaultPoolSize = 8;

    private readonly SemaphoreSlim _gate;
    private readonly ConcurrentStack<DuckDBConnection> _idleConnections = new();
    private readonly ILogger<ReadOnlyDuckDbConnectionPool> _logger;
    private int _disposed;

    public ReadOnlyDuckDbConnectionPool(ILogger<ReadOnlyDuckDbConnectionPool>? logger = null, int? poolSize = null)
    {
        _logger = logger ?? NullLogger<ReadOnlyDuckDbConnectionPool>.Instance;
        int effectiveSize = poolSize ?? ResolveDefaultPoolSize();
        ArgumentOutOfRangeException.ThrowIfLessThan(effectiveSize, 1);
        PoolSize = effectiveSize;
        _gate = new SemaphoreSlim(effectiveSize, effectiveSize);
        _logger.LogDebug("ReadOnlyDuckDbConnectionPool initialized with PoolSize={PoolSize}.", effectiveSize);
    }

    public int PoolSize { get; }

    /// <summary>
    /// Computes the default pool size: <c>min(8, max(2, processorCount / 2))</c>.
    /// </summary>
    public static int ResolveDefaultPoolSize(int? processorCountOverride = null)
    {
        int cores = Math.Max(1, processorCountOverride ?? Environment.ProcessorCount);
        return Math.Min(MaxDefaultPoolSize, Math.Max(MinDefaultPoolSize, cores / 2));
    }

    /// <summary>
    /// Returns <see langword="true"/> only for transient file-sharing locks on an existing Parquet file (Win32 sharing/access locks or
    /// matching DuckDB file-open lock errors), never for missing files or corrupt Parquet payloads.
    /// </summary>
    internal static bool IsTransientParquetLock(Exception error, string filePath)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        if (TransientFileLockRetry.IsTransientLock(error))
        {
            return true;
        }

        if (error is DuckDBException duckEx)
        {
            return ContainsLockMessage(duckEx.Message)
                || (duckEx.InnerException is { } inner && TransientFileLockRetry.IsTransientLock(inner));
        }

        return false;
    }

    private static bool ContainsLockMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        return message.Contains("file is already open", StringComparison.OrdinalIgnoreCase)
            || message.Contains("cannot open file", StringComparison.OrdinalIgnoreCase)
            || message.Contains("used by another process", StringComparison.OrdinalIgnoreCase)
            || message.Contains("sharing violation", StringComparison.OrdinalIgnoreCase)
            || message.Contains("permission denied", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask<IReadOnlyDbConnectionLease> AcquireLeaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        bool handedOut = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            DuckDBConnection? connection = null;
            while (_idleConnections.TryPop(out var candidate))
            {
                if (candidate.State == ConnectionState.Open)
                {
                    connection = candidate;
                    break;
                }

                SafeDispose(candidate);
            }

            if (connection is null)
            {
                connection = new DuckDBConnection("DataSource=:memory:");
                try
                {
                    await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SET threads=1;";
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    SafeDispose(connection);
                    throw;
                }
            }

            var lease = new PooledLease(this, connection);
            handedOut = true;
            return lease;
        }
        finally
        {
            if (!handedOut)
            {
                SafeReleaseGate();
            }
        }
    }

    private void ReturnConnection(DuckDBConnection connection, bool faulted)
    {
        try
        {
            if (faulted || Volatile.Read(ref _disposed) != 0 || connection.State != ConnectionState.Open)
            {
                SafeDispose(connection);
            }
            else
            {
                _idleConnections.Push(connection);
            }
        }
        finally
        {
            SafeReleaseGate();
        }
    }

    private void SafeReleaseGate()
    {
        // SemaphoreSlim is intentionally not disposed in Dispose() (SA_ARCHITECTURE_RULES Disposal Race Guard),
        // so queued WaitAsync waiters wake up cleanly and observe Volatile.Read(ref _disposed) != 0.
        _gate.Release();
    }

    private static void SafeDispose(DuckDBConnection connection)
    {
        try
        {
            connection.Dispose();
        }
        catch (Exception)
        {
            // Disposal best-effort for faulted native connection.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (_idleConnections.TryPop(out var conn))
        {
            SafeDispose(conn);
        }
    }

    private sealed class PooledLease : IReadOnlyDbConnectionLease
    {
        private readonly ReadOnlyDuckDbConnectionPool _pool;
        private readonly DuckDBConnection _connection;
        private int _returned;
        private bool _faulted;

        public PooledLease(ReadOnlyDuckDbConnectionPool pool, DuckDBConnection connection)
        {
            _pool = pool;
            _connection = connection;
        }

        public DbConnection Connection => _connection;

        public void MarkFaulted() => _faulted = true;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) != 0)
            {
                return;
            }

            _pool.ReturnConnection(_connection, _faulted);
        }
    }
}
