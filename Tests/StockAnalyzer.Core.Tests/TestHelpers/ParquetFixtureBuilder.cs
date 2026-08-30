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
    {
        var fixturePath = Path.Combine(dir, $"{ticker}.parquet").Replace("\\", "/");
        var rows = new List<string>();
        decimal close = 100m;
        var date = new DateTime(2024, 1, 1);
        for (int i = 0; i < days; i++)
        {
            close += (i % 2 == 0) ? 1.0m : -0.5m;
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
