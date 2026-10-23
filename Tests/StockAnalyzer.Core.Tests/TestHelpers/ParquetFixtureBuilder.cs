using System;
using System.Collections.Generic;
using System.IO;
using DuckDB.NET.Data;

namespace StockAnalyzer.Core.Tests.TestHelpers;

/// <summary>
/// Builds self-contained synthetic daily OHLCV parquet fixtures for DuckDB-backed tests,
/// so tests never depend on the developer's local (gitignored) Data/TestMarketData folder
/// being populated on this machine or in CI.
/// </summary>
public static class ParquetFixtureBuilder
{
    /// <summary>
    /// Writes a synthetic daily OHLCV parquet for <paramref name="ticker"/> into <paramref name="dir"/>
    /// with an alternating up/down close sequence, so RSI-based screening always has both gains and
    /// losses to work with (an all-gains or all-losses window makes RSI conditions match nothing by design).
    /// </summary>
    public static void SeedDailyOhlcv(string dir, string ticker, int days = 20)
        => SeedDailyOhlcvFromCloses(dir, ticker, ZigZagCloses(days));

    private static IReadOnlyList<decimal> ZigZagCloses(int days)
    {
        var closes = new List<decimal>(days);
        decimal close = 100m;
        for (int i = 0; i < days; i++)
        {
            close += (i % 2 == 0) ? 1.0m : -0.5m;
            closes.Add(close);
        }

        return closes;
    }

    /// <summary>Writes a daily OHLCV parquet whose closes are <paramref name="closes"/> (one bar per consecutive day from 2024-01-01; open/high/low derived as in <see cref="SeedDailyOhlcv"/>).</summary>
    public static void SeedDailyOhlcvFromCloses(string dir, string ticker, IReadOnlyList<decimal> closes)
    {
        var fixturePath = Path.Combine(dir, $"{ticker}.parquet").Replace("\\", "/");
        var rows = new List<string>();
        var date = new DateTime(2024, 1, 1);
        foreach (decimal close in closes)
        {
            var open = close - 0.2m;
            var high = close + 0.5m;
            var low = close - 0.5m;
            rows.Add(FormattableString.Invariant($"(DATE '{date:yyyy-MM-dd}', {open}, {high}, {low}, {close}, 1000)"));
            date = date.AddDays(1);
        }

        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $@"
            COPY (
                SELECT * FROM (VALUES
                    {string.Join(",\n                    ", rows)}
                ) AS t(date, open, high, low, close, volume)
            ) TO '{fixturePath}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }
}
