using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

public sealed class ParquetMonitoringObservationTests
{
    [Theory]
    [InlineData(TimeFrame.D1)]
    [InlineData(TimeFrame.W1)]
    [InlineData(TimeFrame.MN1)]
    public async Task ProviderSnapshot_ReadsAllRowsAndExplicitPeriodFlags(TimeFrame timeframe)
    {
        using var source = new MonitoringParquetFixture();
        source.Write(Bars(), "TRUE", timeframe);
        Assert.Equal(3, (await source.Service.LoadCandlesAsync("X", timeframe, 3)).Count);
        var snapshot = await source.Service.LoadMonitoringObservationAsync("X", timeframe);
        Assert.Equal(200, snapshot!.Candles.Length);
        Assert.All(snapshot.FinalBars, value => Assert.True(value));
        Assert.Equal(TimeSpan.Zero, snapshot.ObservedUtc.Offset);
        Assert.All(snapshot.Candles, candle => Assert.Equal(DateTimeKind.Utc, candle.Timestamp.Kind));
        Assert.Equal(PredictionLogService.SourceRevision(snapshot.Candles), snapshot.SourceRevision);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("FALSE")]
    [InlineData("CAST(NULL AS BOOLEAN)")]
    public async Task NoAttestation_NeverInferredFromOldDatesOrFollowingRows(string? final)
    {
        using var source = new MonitoringParquetFixture();
        source.Write(Bars(), final);
        var snapshot = await source.Service.LoadMonitoringObservationAsync("X", TimeFrame.D1);
        Assert.All(snapshot!.FinalBars, value => Assert.False(value));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("'true'")]
    public async Task NonBooleanAttestation_IsRejected(string final)
    {
        using var source = new MonitoringParquetFixture();
        source.Write(Bars(), final);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.Service.LoadMonitoringObservationAsync("X", TimeFrame.D1));
    }

    [Fact]
    public async Task CancelledMissingAndUnsupportedSources_AreNotTerminalOutcomes()
    {
        using var source = new MonitoringParquetFixture();
        Assert.Null(await source.Service.LoadMonitoringObservationAsync("X", TimeFrame.D1));
        Assert.Null(await source.Service.LoadMonitoringObservationAsync("X", TimeFrame.H1));
        source.Write(Bars(), "TRUE");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.Service.LoadMonitoringObservationAsync(
            "X", TimeFrame.D1, new CancellationToken(true)));
    }

    [Fact]
    public async Task ObservationTime_UsesInjectedClock_AndDefaultsToSystemClock()
    {
        var fixedNow = new DateTimeOffset(2026, 10, 2, 3, 4, 5, TimeSpan.Zero);
        using (var injected = new MonitoringParquetFixture(new RetrainingSchedulerTests.MutableClock(fixedNow)))
        {
            injected.Write(Bars(), "TRUE");
            Assert.Equal(fixedNow, (await injected.Service.LoadMonitoringObservationAsync("X", TimeFrame.D1))!.ObservedUtc);
        }
        using var legacy = new MonitoringParquetFixture();
        legacy.Write(Bars(), "TRUE");
        var before = DateTimeOffset.UtcNow;
        var observed = (await legacy.Service.LoadMonitoringObservationAsync("X", TimeFrame.D1))!.ObservedUtc;
        Assert.InRange(observed, before, DateTimeOffset.UtcNow);
    }

    internal static ImmutableArray<CandleData> Bars(decimal lastClose = 106m) => Enumerable.Range(0, 200)
        .Select(i => new CandleData(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc).AddDays(i - 199),
            i == 199 ? lastClose : 100m, i == 199 ? lastClose : 100m, i == 199 ? lastClose : 100m,
            i == 199 ? lastClose : 100m, 100)).ToImmutableArray();
}

/// <summary>Real source bytes for monitoring acceptance, not a candle or data-provider stub.</summary>
internal sealed class MonitoringParquetFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_t10_source_" + Guid.NewGuid().ToString("N"));
    private readonly DuckDBConnectionManager _db = new();
    private readonly MarketDataSettings _settings;
    internal ParquetDataService Service { get; }
    internal MonitoringParquetFixture(TimeProvider? clock = null)
    {
        _settings = new MarketDataSettings
        {
            DailyDataPath = Path.Combine(_root, "Daily"), WeeklyDataPath = Path.Combine(_root, "Weekly"),
            MonthlyDataPath = Path.Combine(_root, "Monthly")
        };
        Service = new ParquetDataService(_db, Options.Create(_settings), timeProvider: clock);
    }
    internal void Write(IReadOnlyList<CandleData> bars, string? final, TimeFrame timeframe = TimeFrame.D1)
    {
        var folder = timeframe switch
        {
            TimeFrame.W1 => _settings.WeeklyDataPath!, TimeFrame.MN1 => _settings.MonthlyDataPath!,
            _ => _settings.DailyDataPath!
        };
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "X.parquet").Replace("\\", "/").Replace("'", "''");
        static string Decimal(decimal value) => value.ToString(CultureInfo.InvariantCulture);
        var rows = string.Join(",", bars.Select(c => $"(DATE '{c.Timestamp:yyyy-MM-dd}',{Decimal(c.Open)},"
            + $"{Decimal(c.High)},{Decimal(c.Low)},{Decimal(c.Close)},{c.Volume})"));
        using var command = _db.GetConnection().CreateCommand();
        var flag = final is null ? "" : $", {final} AS {ParquetDataService.FinalityColumn}";
        command.CommandText = $"COPY (SELECT *{flag} FROM (VALUES {rows}) "
            + $"AS bars(date,open,high,low,close,volume)) TO '{path}' (FORMAT PARQUET)";
        command.ExecuteNonQuery();
    }
    public void Dispose() { _db.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
