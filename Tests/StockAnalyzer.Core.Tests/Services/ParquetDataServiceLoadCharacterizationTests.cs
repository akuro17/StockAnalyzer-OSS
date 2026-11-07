#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Pins what <see cref="ParquetDataService.LoadCandlesAsync(string, TimeFrame, int)"/> returns and logs for each kind of daily file. The chart, the
/// screener, the backtest and the MCP server all call it, and none of them can tell a missing file from an unreadable one: a failure is logged
/// once and answered with an empty list, cancellation included. These tests were written before the read-status method was added next to it and
/// must stay green unchanged.
/// </summary>
public sealed class ParquetDataServiceLoadCharacterizationTests : IDisposable
{
    internal const string Msft = "MSFT";
    internal const int Days = 20;

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, Msft);
    private readonly DuckDBConnectionManager _db = new();
    private readonly RecordingLogger _log = new();

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
    }

    private ParquetDataService Service() => new(_db, Options.Create(_fixture.Settings), _log);

    private string DailyFile(string symbol) => Path.Combine(_fixture.DailyDirectory, symbol + ".parquet");

    /// <summary>A valid Parquet file with the daily columns and no row.</summary>
    internal static void WriteHeaderOnlyParquet(string path)
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"COPY (SELECT DATE '2024-01-01' AS date, 1.0 AS open, 1.0 AS high, 1.0 AS low, 1.0 AS close, 1 AS volume WHERE false) TO '{path.Replace('\\', '/')}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }

    public enum Kind { Valid, Missing, Corrupt, ZeroBytes, HeaderOnly }

    internal static void Prepare(Kind kind, string path)
    {
        switch (kind)
        {
            case Kind.Missing: File.Delete(path); break;
            case Kind.Corrupt: File.WriteAllText(path, "this is not a parquet file"); break;
            case Kind.ZeroBytes: File.WriteAllBytes(path, Array.Empty<byte>()); break;
            case Kind.HeaderOnly: File.Delete(path); WriteHeaderOnlyParquet(path); break;
        }
    }

    [Theory]
    [InlineData(Kind.Valid, Days, 0)]
    [InlineData(Kind.Missing, 0, 0)]
    [InlineData(Kind.Corrupt, 0, 1)]
    [InlineData(Kind.ZeroBytes, 0, 1)]
    [InlineData(Kind.HeaderOnly, 0, 0)]
    public async Task EachKindOfDailyFileGivesTheSameBarsAndTheSameNumberOfErrorLogsAsBefore(Kind kind, int expectedBars, int expectedErrors)
    {
        Prepare(kind, DailyFile(Msft));

        IReadOnlyList<CandleData> bars = await Service().LoadCandlesAsync(Msft, TimeFrame.D1, 0);

        Assert.Equal(expectedBars, bars.Count);
        Assert.Equal(expectedErrors, _log.Errors.Count);
        if (expectedErrors > 0)
        {
            Assert.All(_log.Errors, e => Assert.Contains(Msft, e.Message, StringComparison.Ordinal));
            Assert.All(_log.Errors, e => Assert.NotNull(e.Exception));
        }
    }

    [Fact]
    public async Task ACancelledCallOnAValidFileIsAnEmptyListWithOneErrorLog_ItIsNotRethrown()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        IReadOnlyList<CandleData> bars = await Service().LoadCandlesAsync(Msft, TimeFrame.D1, 0, includePartialBar: true, cancelled.Token);

        Assert.Empty(bars);
        Assert.Single(_log.Errors);
        Assert.IsAssignableFrom<OperationCanceledException>(_log.Errors[0].Exception);
    }

    [Fact]
    public async Task AFrameThatIsNotStoredAsParquetStillThrowsBeforeAnyFileIsTouched()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => Service().LoadCandlesAsync(Msft, TimeFrame.H1, 0));
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public async Task ACountLimitsTheBarsToTheLatestOnes()
    {
        IReadOnlyList<CandleData> all = await Service().LoadCandlesAsync(Msft, TimeFrame.D1, 0);
        IReadOnlyList<CandleData> latest = await Service().LoadCandlesAsync(Msft, TimeFrame.D1, 5);

        Assert.Equal(all.Skip(Days - 5).ToList(), latest);
    }

    internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    internal sealed class RecordingLogger : ILogger<ParquetDataService>
    {
        public List<LogEntry> Entries { get; } = new();

        public List<LogEntry> Errors => Entries.Where(e => e.Level >= LogLevel.Error).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
