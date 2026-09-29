using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Data;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Manages DuckDB connections, specifically optimized for in-memory operations.
/// </summary>
public sealed class DuckDBConnectionManager : IDisposable
{
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly DuckDBConnection _connection;
    private readonly ILogger<DuckDBConnectionManager>? _logger;
    private readonly object _lock = new();
    private bool _isDisposed;

    public DuckDBConnectionManager(ILogger<DuckDBConnectionManager>? logger = null)
    {
        _logger = logger ?? NullLogger<DuckDBConnectionManager>.Instance;
        // Use in-memory database
        _connection = new DuckDBConnection("DataSource=:memory:");
        _logger.LogDebug("DuckDBConnectionManager initialized with in-memory database.");
    }

    /// <summary>
    /// Acquires a global lock for DuckDB access.
    /// Since the underlying connection is not thread-safe, all users MUST acquire this lock before calling GetConnection().
    /// </summary>
    public async Task<IDisposable> AcquireLockAsync(string context, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _dbLock.WaitAsync(ct);
        if (sw.ElapsedMilliseconds > 500)
        {
            _logger?.LogWarning("DuckDB global lock acquisition took {ElapsedMs}ms for context: {Context}", sw.ElapsedMilliseconds, context);
        }
        return new Releaser(_dbLock);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;
        public void Dispose() => _semaphore.Release();
    }

    public IDbConnection GetConnection()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (_lock)
        {
            if (sw.ElapsedMilliseconds > 100) 
            {
                _logger?.LogWarning("DuckDB internal lock took {ElapsedMs}ms", sw.ElapsedMilliseconds);
            }

            if (_isDisposed)
            {
                throw new ObjectDisposedException(nameof(DuckDBConnectionManager));
            }

            if (_connection.State != ConnectionState.Open)
            {
                _logger?.LogTrace("Opening DuckDB connection.");
                _connection.Open();
                _logger?.LogTrace("DuckDB connection opened in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
            }
            return _connection;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_isDisposed)
            {
                _logger?.LogDebug("Disposing DuckDB connection.");
                _connection.Dispose();
                _dbLock.Dispose();
                _isDisposed = true;
            }
        }
    }
}
