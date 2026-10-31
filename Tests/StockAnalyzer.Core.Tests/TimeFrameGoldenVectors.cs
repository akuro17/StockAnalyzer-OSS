using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests;

/// <summary>One expected weekly or monthly bar of a golden vector: the in-memory timestamp, the stored-file date and the values.</summary>
public sealed record GoldenBar(
    DateTime ChartTimestamp, DateTime ParquetTimestamp, int PeriodKey,
    decimal Open, decimal High, decimal Low, decimal Close, long Volume, bool IsPartial);

/// <summary>A daily series with the weekly and monthly bars every producer must build from it.</summary>
public sealed record GoldenVector(
    string Id, DateTime ReferenceDate, IReadOnlyList<CandleData> DailyBars, IReadOnlyList<GoldenBar> Weekly, IReadOnlyList<GoldenBar> Monthly);

/// <summary>
/// The one loader of <c>Assets/timeframe_golden_vectors.json</c>, the shared definition of how weekly (W1) and monthly (MN1) bars are built from daily bars.
/// Every producer (the aggregator, the Parquet generator, the Python script) is tested against these vectors.
/// </summary>
public static class TimeFrameGoldenVectors
{
    private const string FileName = "timeframe_golden_vectors.json";

    /// <summary>The number of boundary cases the file must hold: a vector removed by mistake fails the aggregator test instead of silently shrinking the definition.</summary>
    public const int RequiredVectorCount = 7;

    public static IReadOnlyList<GoldenVector> Load()
    {
        // The csproj copies the file to the output folder; a missing copy is a build configuration error and is reported as such.
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", FileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Golden vectors file not found at {path} (check CopyToOutputDirectory in the test csproj)", path);
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(ReadVector).ToList();
    }

    /// <summary>The vector ids as xUnit theory data.</summary>
    public static IEnumerable<object[]> Ids() => Load().Select(vector => new object[] { vector.Id });

    public static GoldenVector Get(string id) => Load().Single(vector => vector.Id == id);

    private static GoldenVector ReadVector(JsonElement vector) => new(
        vector.GetProperty("id").GetString()!,
        ParseDate(vector.GetProperty("reference_date")),
        vector.GetProperty("daily_bars").EnumerateArray().Select(bar => new CandleData(
            ParseDate(bar.GetProperty("date")),
            bar.GetProperty("open").GetDecimal(), bar.GetProperty("high").GetDecimal(), bar.GetProperty("low").GetDecimal(),
            bar.GetProperty("close").GetDecimal(), bar.GetProperty("volume").GetInt64())).ToList(),
        vector.GetProperty("expected_weekly").EnumerateArray().Select(ReadBar).ToList(),
        vector.GetProperty("expected_monthly").EnumerateArray().Select(ReadBar).ToList());

    private static GoldenBar ReadBar(JsonElement bar) => new(
        ParseDate(bar.GetProperty("chart_timestamp")), ParseDate(bar.GetProperty("parquet_timestamp")), bar.GetProperty("period_key").GetInt32(),
        bar.GetProperty("open").GetDecimal(), bar.GetProperty("high").GetDecimal(), bar.GetProperty("low").GetDecimal(),
        bar.GetProperty("close").GetDecimal(), bar.GetProperty("volume").GetInt64(), bar.GetProperty("is_partial").GetBoolean());

    private static DateTime ParseDate(JsonElement value) => DateTime.ParseExact(value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
