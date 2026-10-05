using System;
using System.IO;
using System.Threading;
using DuckDB.NET.Data;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>Numerical storage fixtures verify ingress; they are not market training qualification.</summary>
public sealed class RlLocalParquetReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_local_ingress_" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Start = new(2020, 1, 1);

    private string Parquet(string query)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".parquet");
        using var db = new DuckDBConnection("DataSource=:memory:"); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = $"COPY ({query}) TO '{ParquetMarketDataProvider.EscapeDuckDbPath(path.Replace('\\', '/'))}' (FORMAT PARQUET)";
        command.ExecuteNonQuery(); return path;
    }
    private static string Row(string date, string open = "100.123456", string close = "101") =>
        $"SELECT DATE '{date}' AS date, {open}::FLOAT AS open, 110::FLOAT AS high, 90::FLOAT AS low, {close}::FLOAT AS close, 1000::BIGINT AS volume";

    [Fact]
    public void ExistingFloat32UsesCoreDecimalIngressAndExplicitHalfOpenDateSelection()
    {
        string path = Parquet(Row("2020-01-01") + " UNION ALL " + Row("2020-01-02") + " UNION ALL " + Row("2020-01-03"));
        var result = RlLocalParquetReader.Read(path, Start.AddDays(1), Start.AddDays(2), RlTrainingValidation.FreezeLimits(), CancellationToken.None);
        Assert.Single(result.Bars); Assert.Equal(3, result.SourceRows);
        Assert.Equal(Start.AddDays(1), DateOnly.FromDateTime(result.Bars[0].Timestamp));
        Assert.Equal(DateTimeKind.Utc, result.Bars[0].Timestamp.Kind);
        Assert.Equal(TrainingSourceSnapshotProvider.ToDecimal(100.123456f), result.Bars[0].Open);
        Assert.Contains("open:FLOAT", result.StorageDtype);
    }
    [Theory]
    [InlineData("2020-01-02", "2020-01-01")]
    [InlineData("2020-01-01", "2020-01-01")]
    public void UnorderedOrDuplicateStoredDatesAreRejectedEvenOutsideSelectedInterval(string first, string second)
    {
        string path = Parquet(Row(first) + " UNION ALL " + Row(second));
        Assert.Throws<InvalidDataException>(() => RlLocalParquetReader.Read(path, Start.AddDays(5), Start.AddDays(6),
            RlTrainingValidation.FreezeLimits(), CancellationToken.None));
    }
    [Theory]
    [InlineData("NULL")]
    [InlineData("'NaN'")]
    [InlineData("'Infinity'")]
    [InlineData("0")]
    public void MissingNonfiniteOrNonpositiveSelectedPricesAreRejectedWithoutRepair(string open)
    {
        string path = Parquet(Row("2020-01-01", open));
        Assert.Throws<InvalidDataException>(() => RlLocalParquetReader.Read(path, Start, Start.AddDays(1),
            RlTrainingValidation.FreezeLimits(), CancellationToken.None));
    }
    [Fact]
    public void CapturedRowCeilingRejectsInsteadOfTruncatingTheDateInterval()
    {
        string path = Parquet(Row("2020-01-01") + " UNION ALL " + Row("2020-01-02"));
        var limits = RlTrainingValidation.FreezeLimits() with { MaxSamples = 1 };
        Assert.Throws<InvalidDataException>(() => RlLocalParquetReader.Read(path, Start, Start.AddDays(2), limits, CancellationToken.None));
    }
    [Fact]
    public void ExternalPreparedSourcesAndEscapingPathsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => RlTrainingValidation.Freeze(new("NUMERIC_TEST", Start, Start.AddDays(1),
            RlContract.CreateConfiguration(new() { InitialCapital = StockAnalyzer.Core.Models.Backtest.Engine.BacktestDefaults.InitialCapital,
                SizingModel = StockAnalyzer.Core.Models.Backtest.Engine.BacktestDefaults.SizingModel,
                SizingParameter = StockAnalyzer.Core.Models.Backtest.Engine.BacktestDefaults.SizingParameter }), _root)));
        Assert.Throws<InvalidDataException>(() => RlLocalSourceFiles.ValidatePath(Path.Combine(_root, "Data", "Daily"), Path.Combine(_root, "external.parquet")));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
