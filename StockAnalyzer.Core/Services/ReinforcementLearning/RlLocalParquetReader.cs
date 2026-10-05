using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Threading;
using DuckDB.NET.Data;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Strict local Parquet ingress. Shares the established Core floating-source-to-decimal conversion.</summary>
internal static class RlLocalParquetReader
{
    internal sealed record Result(ImmutableArray<CandleData> Bars, string StorageDtype, int SourceRows);

    public static Result Read(string snapshotPath, DateOnly start, DateOnly endExclusive, TrainingResourceLimits limits, CancellationToken ct)
    {
        limits.Validate();
        using var db = new DuckDBConnection("DataSource=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SET threads=" + RlContract.CpuThreads.ToString(CultureInfo.InvariantCulture);
        command.ExecuteNonQuery();
        command.CommandText = "SET memory_limit='" + Math.Min(RlContract.BufferByteLimit,
            checked((long)limits.MaxTensorSizeMiB * TrainingResourceOverrides.MiB)).ToString(CultureInfo.InvariantCulture) + "B'";
        command.ExecuteNonQuery();
        string escaped = ParquetMarketDataProvider.EscapeDuckDbPath(snapshotPath.Replace('\\', '/'));
        command.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{escaped}')";
        var schema = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) schema.Add(reader.GetString(0), reader.GetString(1).ToUpperInvariant());
        foreach (string name in new[] { "date", "open", "high", "low", "close", "volume" })
            if (!schema.TryGetValue(name, out var type)
                || name == "date" && type != "DATE" || name == "volume" && type != "BIGINT"
                || name is "open" or "high" or "low" or "close" && type is not ("FLOAT" or "REAL" or "DOUBLE")
                    && !type.StartsWith("DECIMAL(", StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported local source column/type: {name}.");
        string dtype = string.Join(";", new[] { "open", "high", "low", "close", "volume" }.Select(name => name + ":" + schema[name]));
        command.CommandText = $"SELECT date,open,high,low,close,volume FROM read_parquet('{escaped}')";
        var bars = ImmutableArray.CreateBuilder<CandleData>();
        DateOnly? previous = null;
        int sourceRows = 0;
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested(); sourceRows = checked(sourceRows + 1);
            if (rows.IsDBNull(0)) throw new InvalidDataException("A local date is missing; no row may be silently removed.");
            var date = DateOnly.FromDateTime(rows.GetDateTime(0));
            if (previous is { } last && date <= last) throw new InvalidDataException("Local source dates must be strictly increasing in stored order.");
            previous = date;
            // Explicit date-range selection is independent of the resource ceiling; never silently shorten the requested interval.
            if (date < start || date >= endExclusive) continue;
            for (int field = 1; field < 6; field++)
                if (rows.IsDBNull(field)) throw new InvalidDataException("Missing local OHLCV; no imputation is permitted.");
            var bar = new CandleData(DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
                TrainingSourceSnapshotProvider.ToDecimal(rows.GetValue(1)), TrainingSourceSnapshotProvider.ToDecimal(rows.GetValue(2)),
                TrainingSourceSnapshotProvider.ToDecimal(rows.GetValue(3)), TrainingSourceSnapshotProvider.ToDecimal(rows.GetValue(4)), rows.GetInt64(5));
            if (!bar.IsValid() || bar.Open <= 0m || bar.High <= 0m || bar.Low <= 0m || bar.Close <= 0m)
                throw new InvalidDataException("Invalid local source OHLCV; no repair is permitted.");
            if (bars.Count >= limits.MaxSamples) throw new InvalidDataException("Requested local date interval exceeds the captured row ceiling.");
            bars.Add(bar);
        }
        if (bars.Count == 0) throw new InvalidDataException("InsufficientData: requested local interval is empty.");
        return new(bars.ToImmutable(), dtype, sourceRows);
    }

    public static IReadOnlyDictionary<string, string?> ReadMetadata(string metadataSnapshot)
    {
        using var db = new DuckDBConnection("DataSource=:memory:"); db.Open();
        using var command = db.CreateCommand();
        string escaped = ParquetMarketDataProvider.EscapeDuckDbPath(metadataSnapshot.Replace('\\', '/'));
        command.CommandText = $"SELECT * FROM read_parquet('{escaped}')";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("Local symbol metadata is empty.");
        var result = new Dictionary<string, string?>();
        foreach (string name in new[] { "currency", "quote_type", "exchange_timezone_name" })
        {
            int index = -1;
            for (int i = 0; i < reader.FieldCount; i++) if (reader.GetName(i) == name) { index = i; break; }
            result[name] = index < 0 || reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
        }
        if (reader.Read()) throw new InvalidDataException("Local symbol metadata must contain exactly one row.");
        return result;
    }
}
