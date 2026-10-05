#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>The one DuckDB writer of test Parquet sources (real bytes, not a data-provider stub) shared by the monitoring fixtures.</summary>
internal static class TestParquetWriter
{
    /// <param name="floatPrices">Write prices as FLOAT like the real updater; otherwise as exact decimal literals.</param>
    /// <param name="extraColumn">Optional additional select expression such as <c>true AS is_final</c>.</param>
    public static void Write(DuckDBConnectionManager db, string path, IReadOnlyList<CandleData> bars, bool floatPrices, string? extraColumn = null)
    {
        var sqlPath = path.Replace("\\", "/").Replace("'", "''");
        string Price(decimal v) => floatPrices ? $"CAST({v.ToString(CultureInfo.InvariantCulture)} AS FLOAT)" : v.ToString(CultureInfo.InvariantCulture);
        var rows = string.Join(",", bars.Select(c => $"(DATE '{c.Timestamp:yyyy-MM-dd}',{Price(c.Open)},{Price(c.High)},{Price(c.Low)},{Price(c.Close)},CAST({c.Volume} AS BIGINT))"));
        var extra = extraColumn is null ? "" : $", {extraColumn}";
        using var command = db.GetConnection().CreateCommand();
        command.CommandText = $"COPY (SELECT *{extra} FROM (VALUES {rows}) AS bars(date,open,high,low,close,volume)) TO '{sqlPath}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }
}
