using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using DuckDB.NET.Data;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Real source-snapshot directories (and real local Daily/Metadata Parquet files) for the RL source tests. The numbers are bounded
/// integrity fixtures: they prove validation behavior only and are never market or qualification evidence.
/// </summary>
internal static class RlSourceFixture
{
    public static readonly DateOnly FirstDay = new(2000, 1, 1);

    public static readonly TrainingResourceLimits Limits = new(TrainingResourceLimits.CurrentContractVersion,
        TrainingResourceOverrides.MaximumChannels, TrainingResourceOverrides.MaximumTensorSizeMiB,
        TrainingResourceOverrides.MaximumSamples, TrainingResourceOverrides.MaximumEvaluationFolds,
        TrainingResourceOverrides.MaximumFeatureLag, TrainingResourceOverrides.MaximumEnsembleMembers,
        TrainingResourceOverrides.MaximumRunDurationMinutes * 60);

    /// <summary>Daily bars on consecutive calendar days from <paramref name="first"/>; the price is constant per symbol so OHLC stays valid.</summary>
    public static ImmutableArray<CandleData> Bars(DateOnly first, int count, decimal price = 100m)
        => Enumerable.Range(0, count).Select(i => new CandleData(first.AddDays(i).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            price, price + 10m, price - 10m, price, 1_000)).ToImmutableArray();

    /// <summary>Writes a complete, hash-consistent immutable source snapshot of <paramref name="count"/> bars into a fresh directory below <paramref name="root"/>.</summary>
    public static string WriteSource(string root, int count, string symbol = "FIXTURE", string currency = "USD", string timezone = "UTC",
        DateOnly? first = null, DateOnly? capturedExchangeDate = null, string quoteType = "EQUITY")
    {
        string directory = Path.Combine(root, "source_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        DateOnly start = first ?? FirstDay;
        var bars = Bars(start, count);
        DateOnly end = start.AddDays(count);
        DateOnly captured = capturedExchangeDate ?? end;
        WriteJson(Path.Combine(directory, RlFiles.Bars), bars);
        WriteJson(Path.Combine(directory, RlFiles.Actions), Array.Empty<RlCorporateAction>());
        CreateParquet(Path.Combine(directory, RlFiles.RawParquet),
            $"SELECT DATE '{start:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range({count})");
        CreateParquet(Path.Combine(directory, RlFiles.MetadataParquet),
            $"SELECT '{currency}' AS currency,'{quoteType}' AS quote_type,'{timezone}' AS exchange_timezone_name");
        var stored = RlLocalParquetReader.Read(Path.Combine(directory, RlFiles.RawParquet), start, end, Limits, CancellationToken.None);
        var parameters = RlSourceContract.Acquisition.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value));
        var started = new DateTimeOffset(captured.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero);
        var manifest = new RlSourceManifest(RlSourceContract.SchemaVersion, RlFiles.LocalProvider, RlFiles.LocalProviderVersion, symbol, currency, timezone,
            started, started.AddMinutes(1), ContentHash.OfFile(Path.Combine(directory, RlFiles.Bars)), ContentHash.OfFile(Path.Combine(directory, RlFiles.Actions)),
            bars.Length, RlSourceContract.Adjustment, RlSourceContract.LocalProvenance, RlSourceContract.Unverified, stored.StorageDtype,
            RlSourceContract.FinalityPolicy, captured, start, end, parameters)
        {
            InstrumentType = quoteType, MappedSymbol = symbol, SourcePathIdentity = "Daily/" + symbol + ".parquet",
            SourceFileSha256 = ContentHash.OfFile(Path.Combine(directory, RlFiles.RawParquet)), CurrentHistoryRevision = false,
            MetadataPathIdentity = "Metadata/" + symbol + ".meta.parquet",
            MetadataFileSha256 = ContentHash.OfFile(Path.Combine(directory, RlFiles.MetadataParquet)),
            SourceRows = stored.SourceRows, EvidencePolicy = RlSourceContract.EvidencePolicy,
            OriginalAcquisitionStatus = RlSourceContract.Unverified, CorporateActionCoverageStatus = RlSourceContract.Unverified,
            DateLabelSemantics = RlSourceContract.DateLabelSemantics,
            FirstDate = DateOnly.FromDateTime(bars[0].Timestamp), LastDate = DateOnly.FromDateTime(bars[^1].Timestamp),
            ActionsPeriodStart = null, ActionsPeriodEnd = null,
        };
        WriteJson(Path.Combine(directory, RlFiles.SourceManifest), manifest);
        return directory;
    }

    /// <summary>Writes real Data/Daily and Data/Metadata Parquet files for one symbol below the (isolated) data root.</summary>
    public static void WriteLocalSymbol(string dataRoot, string symbol, DateOnly first, int count, string currency = "USD",
        string timezone = "UTC", string quoteType = "EQUITY", bool metadata = true)
    {
        string daily = Path.Combine(dataRoot, "Daily"), meta = Path.Combine(dataRoot, "Metadata");
        Directory.CreateDirectory(daily); Directory.CreateDirectory(meta);
        CreateParquet(Path.Combine(daily, symbol + ".parquet"),
            $"SELECT DATE '{first:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume FROM range({count})");
        if (metadata)
            CreateParquet(Path.Combine(meta, symbol + ".meta.parquet"),
                $"SELECT '{currency}' AS currency,'{quoteType}' AS quote_type,'{timezone}' AS exchange_timezone_name");
    }

    /// <summary>
    /// Writes a symbol whose prices change from bar to bar (a model reacts to them, unlike the constant series of <see cref="WriteLocalSymbol"/>).
    /// Every price (not the volume) of the bars with index <paramref name="scaleFromIndex"/> to <paramref name="scaleToIndex"/> (inclusive) are
    /// multiplied by <paramref name="factor"/>; the default range is empty. OHLC stays valid for any positive factor.
    /// </summary>
    public static void WriteVaryingSymbol(string dataRoot, string symbol, DateOnly first, int count,
        int scaleFromIndex = int.MaxValue, int scaleToIndex = int.MaxValue, decimal factor = 1.7m)
    {
        WriteLocalSymbol(dataRoot, symbol, first, 1);
        string k = $"(CASE WHEN range BETWEEN {scaleFromIndex} AND {scaleToIndex} THEN {factor.ToString(CultureInfo.InvariantCulture)} ELSE 1.0 END)";
        string level = "(100 + range % 23)";
        CreateParquet(Path.Combine(dataRoot, "Daily", symbol + ".parquet"),
            $"SELECT DATE '{first:yyyy-MM-dd}'+CAST(range AS INTEGER) AS date,({level}*{k})::FLOAT AS open,(({level}+5)*{k})::FLOAT AS high," +
            $"(({level}-5)*{k})::FLOAT AS low,(({level}+(range % 5)-2)*{k})::FLOAT AS close,(1000+range % 7*10)::BIGINT AS volume FROM range({count})");
    }

    /// <summary>
    /// Writes a symbol that trades Monday to Friday only (no bar on a Saturday or Sunday), starting on <paramref name="firstMonday"/> and covering
    /// <paramref name="calendarDays"/> calendar days; returns its trading days in order.
    /// </summary>
    public static System.Collections.Generic.List<DateOnly> WriteWeekdaySymbol(string dataRoot, string symbol, DateOnly firstMonday, int calendarDays)
    {
        if (firstMonday.DayOfWeek != DayOfWeek.Monday) throw new ArgumentException("The first day must be a Monday.", nameof(firstMonday));
        WriteLocalSymbol(dataRoot, symbol, firstMonday, 1);
        CreateParquet(Path.Combine(dataRoot, "Daily", symbol + ".parquet"),
            $"SELECT d AS date,100::FLOAT AS open,110::FLOAT AS high,90::FLOAT AS low,100::FLOAT AS close,1000::BIGINT AS volume " +
            $"FROM (SELECT DATE '{firstMonday:yyyy-MM-dd}'+CAST(range AS INTEGER) AS d FROM range({calendarDays})) WHERE dayofweek(d) BETWEEN 1 AND 5 ORDER BY d");
        return Enumerable.Range(0, calendarDays).Select(i => firstMonday.AddDays(i)).Where(d => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)).ToList();
    }

    public static void Mutate(string directory, string file, Action<JsonNode> change)
    {
        string path = Path.Combine(directory, file);
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        change(root);
        File.WriteAllText(path, root.ToJsonString());
    }

    public static void WriteJson<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, RlFiles.Json));

    public static void CreateParquet(string path, string query)
    {
        using var db = new DuckDBConnection("DataSource=:memory:"); db.Open(); using var command = db.CreateCommand();
        command.CommandText = "SET threads=" + RlContract.CpuThreads.ToString(CultureInfo.InvariantCulture); command.ExecuteNonQuery();
        command.CommandText = $"COPY ({query}) TO '{ParquetMarketDataProvider.EscapeDuckDbPath(path.Replace('\\', '/'))}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }

    /// <summary>Hash of the whole directory content (relative path + bytes) to prove a load or listing never wrote anything.</summary>
    public static string TreeDigest(string directory)
    {
        var lines = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(directory, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => f + ":" + ContentHash.OfFile(Path.Combine(directory, f)) + ":" + File.GetLastWriteTimeUtc(Path.Combine(directory, f)).Ticks);
        return ContentHash.OfText(string.Join("\n", lines));
    }
}
