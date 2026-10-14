using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Threading;
using DuckDB.NET.Data;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// One open in-memory DuckDB connection that reads the dates (and the metadata) of many local Parquet files one after another. Opening a connection costs
/// far more than reading the dates of one file, so a caller that reads many files keeps one session; <see cref="RlLocalParquetReader.ReadDates"/> opens a
/// session per call, so the single-file behaviour is unchanged. Not thread-safe: one reader at a time, on one thread. A file that fails (a broken calendar, a
/// file DuckDB rejects) throws from that call only; the session stays usable for the next file.
/// </summary>
internal sealed class RlLocalParquetSession : IDisposable
{
    private readonly DuckDBConnection _db = new("DataSource=:memory:");

    public RlLocalParquetSession(TrainingResourceLimits limits)
    {
        limits.Validate();
        try
        {
            _db.Open();
            using var command = _db.CreateCommand();
            command.CommandText = "SET threads=" + RlContract.CpuThreads.ToString(CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
            command.CommandText = "SET memory_limit='" + Math.Min(RlContract.BufferByteLimit,
                checked((long)limits.MaxTensorSizeMiB * TrainingResourceOverrides.MiB)).ToString(CultureInfo.InvariantCulture) + "B'";
            command.ExecuteNonQuery();
        }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    /// <summary>The stored dates before <paramref name="endExclusive"/> (see <see cref="RlLocalParquetReader.ReadDates"/>).</summary>
    public ImmutableArray<DateOnly> ReadDates(string snapshotPath, DateOnly endExclusive, CancellationToken ct)
    {
        using var command = _db.CreateCommand();
        string escaped = ParquetMarketDataProvider.EscapeDuckDbPath(snapshotPath.Replace('\\', '/'));
        command.CommandText = $"DESCRIBE SELECT date FROM read_parquet('{escaped}')";
        using (var schema = command.ExecuteReader())
            if (!schema.Read() || schema.GetString(0) != "date" || !string.Equals(schema.GetString(1), "DATE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported local source column/type: date.");
        command.CommandText = $"SELECT date FROM read_parquet('{escaped}')";
        var dates = ImmutableArray.CreateBuilder<DateOnly>();
        DateOnly? previous = null;
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (rows.IsDBNull(0)) throw new InvalidDataException("A local date is missing; no row may be silently removed.");
            var date = DateOnly.FromDateTime(rows.GetDateTime(0));
            if (previous is { } last && date <= last) throw new InvalidDataException("Local source dates must be strictly increasing in stored order.");
            previous = date;
            if (date < endExclusive) dates.Add(date);
        }
        return dates.ToImmutable();
    }

    /// <summary>The currency, quote type and exchange time zone of the symbol (see <see cref="RlLocalParquetReader.ReadMetadata"/>).</summary>
    public IReadOnlyDictionary<string, string?> ReadMetadata(string metadataSnapshot) => RlLocalParquetReader.ReadMetadata(_db, metadataSnapshot);

    public void Dispose() => _db.Dispose();
}
