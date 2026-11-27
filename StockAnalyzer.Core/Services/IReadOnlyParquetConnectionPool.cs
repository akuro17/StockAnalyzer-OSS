using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// A scoped, exclusive lease of a pooled read-only DuckDB connection. Disposing the lease returns a healthy connection to the pool,
/// or disposes and discards a connection marked faulted.
/// </summary>
public interface IReadOnlyDbConnectionLease : IDisposable
{
    DbConnection Connection { get; }

    /// <summary>
    /// Marks the leased connection as faulted (for example after cancellation or query error) so it is discarded on disposal instead of
    /// being reused by future readers.
    /// </summary>
    void MarkFaulted();
}

/// <summary>
/// Bounded pool of independent in-memory DuckDB read connections for concurrent <c>read_parquet</c> chart and indicator queries.
/// Write paths and large batch analytical queries continue using <see cref="DuckDBConnectionManager"/>.
/// </summary>
public interface IReadOnlyParquetConnectionPool : IDisposable
{
    int PoolSize { get; }

    ValueTask<IReadOnlyDbConnectionLease> AcquireLeaseAsync(CancellationToken cancellationToken = default);
}
