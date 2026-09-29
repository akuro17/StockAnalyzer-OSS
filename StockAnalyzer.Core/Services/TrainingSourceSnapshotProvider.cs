using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>Checks provider-attested bar finality and fixes both trainer inputs to copied Parquet bytes.</summary>
public sealed class TrainingSourceSnapshotProvider : ITrainingSourceSnapshotProvider
{
    private const int ManifestVersion = 1;
    private const int InsertBatchSize = 1024;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task<PreparedTrainingInput> PrepareAsync(TrainingJobConfig config, string runDirectory,
        DateTimeOffset jobStartedUtc, IProgress<string>? stage = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.RequiresFinalizedSource || string.IsNullOrWhiteSpace(config.SourceManifestPath))
            throw new InvalidOperationException("Prepared input requires weekly/monthly composed indicator training and a manifest.");
        stage?.Report("Validating");
        var manifestPath = Path.GetFullPath(config.SourceManifestPath);
        var root = Path.GetDirectoryName(manifestPath)!;
        var json = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
        ValidateUtcSpelling(json);
        var manifest = JsonSerializer.Deserialize<SourceManifest>(json, TrainingConfigJson.Options)
            ?? throw new InvalidDataException("Source manifest must be an object.");
        if (manifest.SchemaVersion != ManifestVersion || string.IsNullOrWhiteSpace(manifest.ProviderId)
            || string.IsNullOrWhiteSpace(manifest.DatasetRevision) || manifest.Symbols is not { Count: > 0 }
            || manifest.Timeframe != config.Timeframe.ToString().ToLowerInvariant())
            throw new InvalidDataException("Source manifest version, identity, timeframe or symbols are invalid.");
        RequireUtc(manifest.AsOfUtc, "as_of_utc");
        if (manifest.AsOfUtc > jobStartedUtc)
            throw new InvalidDataException("Source as_of_utc is after job start.");

        var entries = new Dictionary<string, SourceEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Symbols)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Symbol)
                || !Regex.IsMatch(entry.Symbol, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z")
                || entry.Symbol is "." or ".."
                || WindowsDeviceFileName.IsReserved(entry.Symbol.Split('.')[0])
                || !entries.TryAdd(entry.Symbol, entry))
                throw new InvalidDataException("Source manifest has a duplicate or invalid symbol.");
            if (string.IsNullOrWhiteSpace(entry.Path) || string.IsNullOrWhiteSpace(entry.Sha256)
                || !Regex.IsMatch(entry.Sha256, @"\A[0-9a-fA-F]{64}\z")
                || entry.RowCount <= 0 || entry.BarEvidence is null
                || entry.BarEvidence.Count != entry.RowCount)
                throw new InvalidDataException($"Source manifest entry for '{entry.Symbol}' is incomplete.");
            ValidateEvidence(entry.BarEvidence, manifest.AsOfUtc);
        }
        if (config.Symbols.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Symbols.Length)
            throw new InvalidDataException("Requested symbols contain duplicates.");
        foreach (var symbol in config.Symbols)
            if (!entries.ContainsKey(symbol))
                throw new InvalidDataException($"Source manifest is missing '{symbol}'.");

        stage?.Report("Snapshotting");
        var snapshots = Path.Combine(runDirectory, "snapshot");
        var dataset = Path.Combine(runDirectory, "dataset");
        Directory.CreateDirectory(snapshots);
        Directory.CreateDirectory(dataset);
        var result = new Dictionary<string, PreparedTrainingSymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var symbol in config.Symbols)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries[symbol];
            var source = ResolveSource(root, entry.Path!);
            var copy = Path.Combine(snapshots, symbol + ".parquet");
            File.Copy(source, copy, overwrite: false);
            string hash;
            await using (var copied = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(copied, ct).ConfigureAwait(false));
            if (!hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Source SHA-256 mismatch for '{symbol}'.");
            var candles = ReadCandles(copy, entry);
            ct.ThrowIfCancellationRequested();
            var prepared = Path.Combine(dataset, symbol + ".parquet");
            WritePrepared(prepared, candles);
            result.Add(symbol, new PreparedTrainingSymbol(hash, copy, prepared, candles.AsReadOnly()));
        }
        return new PreparedTrainingInput(runDirectory, dataset, manifest.ProviderId!, manifest.DatasetRevision!,
            manifest.AsOfUtc, new ReadOnlyDictionary<string, PreparedTrainingSymbol>(result));
    }

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value == default || value.Offset != TimeSpan.Zero)
            throw new InvalidDataException($"{name} must be UTC.");
    }

    private static void ValidateUtcSpelling(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Source manifest must be an object.");
        RequireUtcSpelling(root.GetProperty("as_of_utc"));
        foreach (var symbol in root.GetProperty("symbols").EnumerateArray())
            foreach (var bar in symbol.GetProperty("bar_evidence").EnumerateArray())
            {
                RequireUtcSpelling(bar.GetProperty("period_start_utc"));
                RequireUtcSpelling(bar.GetProperty("period_end_utc"));
                RequireUtcSpelling(bar.GetProperty("available_at_utc"));
            }
    }

    private static void RequireUtcSpelling(JsonElement value)
    {
        var timestamp = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (timestamp is null || !(timestamp.EndsWith("Z", StringComparison.OrdinalIgnoreCase)
            || timestamp.EndsWith("+00:00", StringComparison.Ordinal)))
            throw new InvalidDataException("Manifest timestamps must explicitly use UTC (Z or +00:00).");
    }

    private static void ValidateEvidence(IReadOnlyList<BarEvidence?> bars, DateTimeOffset asOf)
    {
        DateOnly? lastDate = null;
        DateTimeOffset? lastEnd = null;
        foreach (var bar in bars)
        {
            if (bar is null || bar.Date == default || !bar.IsFinal)
                throw new InvalidDataException("Bar evidence is absent or not final.");
            RequireUtc(bar.PeriodStartUtc, "period_start_utc");
            RequireUtc(bar.PeriodEndUtc, "period_end_utc");
            RequireUtc(bar.AvailableAtUtc, "available_at_utc");
            if (bar.PeriodStartUtc >= bar.PeriodEndUtc || bar.PeriodEndUtc > bar.AvailableAtUtc
                || bar.AvailableAtUtc > asOf || lastDate >= bar.Date || lastEnd > bar.PeriodStartUtc)
                throw new InvalidDataException("Bar evidence has invalid chronology, overlap or future availability.");
            lastDate = bar.Date;
            lastEnd = bar.PeriodEndUtc;
        }
    }

    private static string ResolveSource(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Parquet path must be relative.");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var rootPrefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootPrefix, PathComparison))
            throw new InvalidDataException("Parquet path escapes the manifest directory.");
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(full)!); dir is not null
             && dir.FullName.StartsWith(fullRoot, PathComparison); dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Parquet path uses a directory link.");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Parquet path uses a file link.");
        return full;
    }

    private static List<CandleData> ReadCandles(string path, SourceEntry entry)
    {
        using var db = new DuckDBConnection("DataSource=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        var escaped = ParquetMarketDataProvider.EscapeDuckDbPath(path.Replace("\\", "/"));
        command.CommandText = $"DESCRIBE SELECT * FROM read_parquet('{escaped}')";
        var schema = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = command.ExecuteReader())
            while (reader.Read()) schema.Add(reader.GetString(0), reader.GetString(1).ToUpperInvariant());
        foreach (var name in new[] { "date", "open", "high", "low", "close", "volume" })
        {
            if (!schema.TryGetValue(name, out var type)
                || name == "date" && type != "DATE"
                || name == "volume" && type != "BIGINT"
                || name is "open" or "high" or "low" or "close"
                    && type is not ("FLOAT" or "REAL" or "DOUBLE")
                    && !type.StartsWith("DECIMAL(", StringComparison.Ordinal))
                throw new InvalidDataException($"Source column '{name}' has a missing or unsupported type.");
        }
        command.CommandText = $"SELECT date, open, high, low, close, volume FROM read_parquet('{escaped}')";
        var candles = new List<CandleData>(entry.RowCount);
        using var rows = command.ExecuteReader();
        while (rows.Read())
        {
            var index = candles.Count;
            if (index >= entry.RowCount || Enumerable.Range(0, 6).Any(rows.IsDBNull))
                throw new InvalidDataException("Source row count or required null is invalid.");
            var date = DateOnly.FromDateTime(rows.GetDateTime(0));
            if (date != entry.BarEvidence![index]!.Date)
                throw new InvalidDataException("Parquet date does not match bar evidence in row order.");
            var candle = new CandleData(date.ToDateTime(TimeOnly.MinValue), ToDecimal(rows.GetValue(1)),
                ToDecimal(rows.GetValue(2)), ToDecimal(rows.GetValue(3)), ToDecimal(rows.GetValue(4)),
                rows.GetInt64(5));
            if (candle.Volume < 0)
                throw new InvalidDataException("Source volume must be nonnegative.");
            candles.Add(candle);
        }
        if (candles.Count != entry.RowCount)
            throw new InvalidDataException("Source row count differs from evidence.");
        return candles;
    }

    private static decimal ToDecimal(object value)
    {
        try
        {
            if (value is float f && !float.IsFinite(f) || value is double d && !double.IsFinite(d))
                throw new InvalidDataException("Non-finite source price.");
            return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or InvalidCastException)
        {
            throw new InvalidDataException("Source price cannot be represented as decimal.", ex);
        }
    }

    private static void WritePrepared(string path, IReadOnlyList<CandleData> candles)
    {
        using var db = new DuckDBConnection("DataSource=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE input (date DATE, open DOUBLE, high DOUBLE, low DOUBLE, close DOUBLE, volume BIGINT)";
        command.ExecuteNonQuery();
        for (var start = 0; start < candles.Count; start += InsertBatchSize)
        {
            var values = candles.Skip(start).Take(InsertBatchSize).Select(c => FormattableString.Invariant(
                $"(DATE '{c.Timestamp:yyyy-MM-dd}', CAST({c.Open} AS DOUBLE), CAST({c.High} AS DOUBLE), CAST({c.Low} AS DOUBLE), CAST({c.Close} AS DOUBLE), {c.Volume})"));
            command.CommandText = "INSERT INTO input VALUES " + string.Join(",", values);
            command.ExecuteNonQuery();
        }
        command.CommandText = $"COPY (SELECT * FROM input ORDER BY date) TO '{ParquetMarketDataProvider.EscapeDuckDbPath(path.Replace("\\", "/"))}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }

    private sealed record SourceManifest
    {
        public int SchemaVersion { get; init; }
        public string? ProviderId { get; init; }
        public string? DatasetRevision { get; init; }
        public DateTimeOffset AsOfUtc { get; init; }
        public string? Timeframe { get; init; }
        public List<SourceEntry?>? Symbols { get; init; }
    }
    private sealed record SourceEntry
    {
        public string? Symbol { get; init; }
        public string? Path { get; init; }
        public string? Sha256 { get; init; }
        public int RowCount { get; init; }
        public List<BarEvidence?>? BarEvidence { get; init; }
    }
    private sealed record BarEvidence
    {
        public DateOnly Date { get; init; }
        public DateTimeOffset PeriodStartUtc { get; init; }
        public DateTimeOffset PeriodEndUtc { get; init; }
        public DateTimeOffset AvailableAtUtc { get; init; }
        public bool IsFinal { get; init; }
    }
}
