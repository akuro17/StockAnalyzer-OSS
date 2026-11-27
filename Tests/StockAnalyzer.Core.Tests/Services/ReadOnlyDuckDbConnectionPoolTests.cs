using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;
using static StockAnalyzer.Core.Tests.Services.ParquetDataServiceLoadCharacterizationTests;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ReadOnlyDuckDbConnectionPoolTests : IDisposable
{
    private const int Days = 30;
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "AAPL", "MSFT", "NVDA", "SPY");
    private readonly DuckDBConnectionManager _db = new();
    private readonly ReadOnlyDuckDbConnectionPool _pool = new(poolSize: 2);
    private readonly RecordingLogger _log = new();

    public void Dispose()
    {
        _pool.Dispose();
        _db.Dispose();
        _fixture.Dispose();
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(4, 2)]
    [InlineData(6, 3)]
    [InlineData(12, 6)]
    [InlineData(16, 8)]
    [InlineData(32, 8)]
    public void ResolveDefaultPoolSize_MatchesFormulaBounds(int cores, int expectedPoolSize)
    {
        Assert.Equal(expectedPoolSize, ReadOnlyDuckDbConnectionPool.ResolveDefaultPoolSize(cores));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Constructor_NonPositivePoolSize_Throws(int invalidSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReadOnlyDuckDbConnectionPool(poolSize: invalidSize));
    }

    [Fact]
    public async Task AcquireLease_SetsSingleDuckDbThreadAndBoundsConcurrency()
    {
        using var singlePool = new ReadOnlyDuckDbConnectionPool(poolSize: 1);

        var lease1 = await singlePool.AcquireLeaseAsync();
        using (var cmd = lease1.Connection.CreateCommand())
        {
            cmd.CommandText = "SELECT current_setting('threads');";
            var threadsVal = await cmd.ExecuteScalarAsync();
            Assert.Equal(1L, Convert.ToInt64(threadsVal));
        }

        var blockedTask = singlePool.AcquireLeaseAsync().AsTask();
        Assert.False(blockedTask.IsCompleted);

        lease1.Dispose();
        using var lease2 = await blockedTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(lease2.Connection);
    }

    [Fact]
    public async Task MarkFaulted_DisposesCorruptConnectionAndReopensFreshConnection()
    {
        using var singlePool = new ReadOnlyDuckDbConnectionPool(poolSize: 1);

        var lease1 = await singlePool.AcquireLeaseAsync();
        var conn1 = lease1.Connection;
        lease1.MarkFaulted();
        lease1.Dispose();

        using var lease2 = await singlePool.AcquireLeaseAsync();
        Assert.NotSame(conn1, lease2.Connection);
    }

    [Fact]
    public async Task CancelledWait_DoesNotLeakSemaphoreSlot()
    {
        using var singlePool = new ReadOnlyDuckDbConnectionPool(poolSize: 1);
        using var held = await singlePool.AcquireLeaseAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await singlePool.AcquireLeaseAsync(cts.Token));

        held.Dispose();
        using var next = await singlePool.AcquireLeaseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next.Connection);
    }

    [Fact]
    public async Task DisposedPool_RejectsNewAcquire_AndHandlesInFlightLeaseReturnSafely()
    {
        var tempPool = new ReadOnlyDuckDbConnectionPool(poolSize: 1);
        var activeLease = await tempPool.AcquireLeaseAsync();

        tempPool.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await tempPool.AcquireLeaseAsync());

        // Returning an already-acquired lease after pool disposal must not throw.
        activeLease.Dispose();
    }

    [Fact]
    public async Task DisposedPool_DoesNotHangQueuedWaiters_AndThrowsObjectDisposedException()
    {
        var tempPool = new ReadOnlyDuckDbConnectionPool(poolSize: 1);
        var heldLease = await tempPool.AcquireLeaseAsync();

        var queuedTask = tempPool.AcquireLeaseAsync().AsTask();
        Assert.False(queuedTask.IsCompleted);

        tempPool.Dispose();
        heldLease.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => queuedTask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task IsTransientParquetLock_DetectsExistingLockButRejectsMissingOrCorruptErrors()
    {
        string existingFile = Path.Combine(_fixture.DailyDirectory, "AAPL.parquet");
        string missingFile = Path.Combine(_fixture.DailyDirectory, "DOES_NOT_EXIST.parquet");
        string corruptFile = Path.Combine(_fixture.DailyDirectory, "NVDA.parquet");
        Prepare(Kind.Corrupt, corruptFile);

        var sharingIo = new IOException("The process cannot access the file because it is being used by another process.")
        {
            HResult = unchecked((int)0x80070020)
        };

        Assert.Equal(OperatingSystem.IsWindows(), ReadOnlyDuckDbConnectionPool.IsTransientParquetLock(sharingIo, existingFile));
        Assert.False(ReadOnlyDuckDbConnectionPool.IsTransientParquetLock(sharingIo, missingFile));

        // Native DuckDB exception on corrupt Parquet must NOT be treated as a transient sharing lock.
        using var lease = await _pool.AcquireLeaseAsync();
        using var corruptCmd = lease.Connection.CreateCommand();
        corruptCmd.CommandText = $"SELECT * FROM read_parquet('{corruptFile.Replace("'", "''")}')";
        var corruptEx = await Assert.ThrowsAsync<DuckDBException>(() => corruptCmd.ExecuteScalarAsync());
        Assert.False(ReadOnlyDuckDbConnectionPool.IsTransientParquetLock(corruptEx, corruptFile));

        if (OperatingSystem.IsWindows())
        {
            // Exclusive lock triggers a real native DuckDB file-open error on an existing file.
            using var exclusiveLock = new FileStream(existingFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var lockedCmd = lease.Connection.CreateCommand();
            lockedCmd.CommandText = $"SELECT * FROM read_parquet('{existingFile.Replace("'", "''")}')";
            var lockEx = await Assert.ThrowsAsync<DuckDBException>(() => lockedCmd.ExecuteScalarAsync());
            Assert.True(ReadOnlyDuckDbConnectionPool.IsTransientParquetLock(lockEx, existingFile),
                $"Actual DuckDBException.Message: {lockEx.Message}");
        }
    }

    [Fact]
    public async Task PooledParquetDataService_MatchesLegacyReadsUnderConcurrency()
    {
        var legacy = new ParquetDataService(_db, Options.Create(_fixture.Settings));
        var pooled = new ParquetDataService(_db, Options.Create(_fixture.Settings), _log, readPool: _pool);

        var expectedMsft = await legacy.LoadCandlesWithStatusAsync("MSFT", TimeFrame.D1, 20);
        var tasks = Enumerable.Range(0, 8)
            .Select(i => pooled.LoadCandlesWithStatusAsync(i % 2 == 0 ? "MSFT" : "AAPL", TimeFrame.D1, 20));
        var results = await Task.WhenAll(tasks);

        Assert.Equal(expectedMsft.Candles, results[0].Candles);
        Assert.All(results, r =>
        {
            Assert.Equal(DataReadStatus.Read, r.Status);
            Assert.Equal(20, r.Candles.Count);
        });

        string corruptPath = Path.Combine(_fixture.DailyDirectory, "NVDA.parquet");
        Prepare(Kind.Corrupt, corruptPath);
        var corruptResult = await pooled.LoadCandlesWithStatusAsync("NVDA", TimeFrame.D1, 0);
        Assert.Equal(DataReadStatus.Unreadable, corruptResult.Status);
        Assert.Empty(corruptResult.Candles);

        var missingResult = await pooled.LoadCandlesWithStatusAsync("UNKNOWN_TICKER", TimeFrame.D1, 0);
        Assert.Equal(DataReadStatus.FileMissing, missingResult.Status);
    }
}
