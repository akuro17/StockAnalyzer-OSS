#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>
/// Pins <see cref="RlLocalSnapshotFactory.PrepareAsync"/> on real local Daily/Metadata Parquet files in an isolated Data root, before
/// its capture-and-write part was extracted for the panel snapshot. The same tests must pass unchanged afterwards.
/// </summary>
[Collection("PathDiscovery UIA environment")]
public sealed class RlLocalSnapshotFactoryCharacterizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sa_rl_factory_" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousEnabled = Environment.GetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable);
    private readonly string? _previousRoot = Environment.GetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable);
    private string DataRoot => Path.Combine(_root, "data");
    private string Snapshot(string name = "snapshot") => Path.Combine(_root, name);

    public RlLocalSnapshotFactoryCharacterizationTests()
    {
        Directory.CreateDirectory(DataRoot);
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, "1");
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, DataRoot);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestDataRootEnvironmentVariable, _previousRoot);
        Environment.SetEnvironmentVariable(PathDiscovery.UiTestEnabledEnvironmentVariable, _previousEnabled);
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static RlTrainingJob Job(string symbol, DateOnly start, DateOnly end, bool smoke = false)
        => new(symbol, start, end, new BacktestConfiguration(), null, smoke);

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task CapturesCopiesAndWritesTheDocumentedFiles()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", RlSourceFixture.FirstDay, 5_200);
        DateOnly end = RlSourceFixture.FirstDay.AddDays(5_200);
        DateOnly before = Today;
        var snapshot = await RlLocalSnapshotFactory.PrepareAsync(Job("ABC", RlSourceFixture.FirstDay, end), Snapshot(), RlSourceFixture.Limits, CancellationToken.None);
        DateOnly after = Today;

        Assert.Equal(new[] { RlFiles.Actions, RlFiles.Bars, RlFiles.MetadataParquet, RlFiles.RawParquet, RlFiles.SourceManifest }.OrderBy(f => f, StringComparer.Ordinal),
            Directory.EnumerateFiles(Snapshot()).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal));
        Assert.Equal(ContentHash.OfFile(Path.Combine(DataRoot, "Daily", "ABC.parquet")), ContentHash.OfFile(Path.Combine(Snapshot(), RlFiles.RawParquet)));
        Assert.Equal(ContentHash.OfFile(Path.Combine(DataRoot, "Metadata", "ABC.meta.parquet")), ContentHash.OfFile(Path.Combine(Snapshot(), RlFiles.MetadataParquet)));
        Assert.Equal(ContentHash.OfText("[]"), ContentHash.OfFile(Path.Combine(Snapshot(), RlFiles.Actions)));
        Assert.Equal(ContentHash.OfText(JsonSerializer.Serialize(RlSourceFixture.Bars(RlSourceFixture.FirstDay, 5_200), RlFiles.Json)),
            ContentHash.OfFile(Path.Combine(Snapshot(), RlFiles.Bars)));

        var manifest = snapshot.Manifest;
        Assert.Equal("ABC", manifest.Symbol);
        Assert.Equal("ABC", manifest.MappedSymbol);
        Assert.Equal("Daily/ABC.parquet", manifest.SourcePathIdentity);
        Assert.Equal("Metadata/ABC.meta.parquet", manifest.MetadataPathIdentity);
        Assert.Equal("USD", manifest.QuoteCurrency);
        Assert.Equal("UTC", manifest.ExchangeTimezone);
        Assert.Equal("EQUITY", manifest.InstrumentType);
        Assert.Equal(5_200, manifest.RowCount);
        Assert.Equal(5_200, manifest.SourceRows);
        Assert.Equal(RlSourceFixture.FirstDay, manifest.PeriodStartInclusive);
        Assert.Equal(end, manifest.PeriodEndExclusive);
        Assert.Equal(RlSourceFixture.FirstDay, manifest.FirstDate);
        Assert.Equal(end.AddDays(-1), manifest.LastDate);
        Assert.InRange(manifest.CapturedExchangeDate, before, after);
        Assert.Equal(ContentHash.OfFile(Path.Combine(Snapshot(), RlFiles.SourceManifest)), snapshot.Revision);
        Assert.Equal((16, 5, 5), (snapshot.Catalog.Train.Length, snapshot.Catalog.Validation.Length, snapshot.Catalog.Oos.Length));
    }

    [Fact]
    public async Task ThePeriodSelectsTheStoredBars()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", RlSourceFixture.FirstDay, 5_200);
        var start = RlSourceFixture.FirstDay.AddDays(10);
        var snapshot = await RlLocalSnapshotFactory.PrepareAsync(Job("ABC", start, RlSourceFixture.FirstDay.AddDays(5_200)), Snapshot(), RlSourceFixture.Limits, CancellationToken.None);
        Assert.Equal(5_190, snapshot.Bars.Length);
        Assert.Equal(start, snapshot.Manifest.FirstDate);
        Assert.Equal(5_200, snapshot.Manifest.SourceRows);
    }

    [Fact]
    public async Task ShortHistoryAndSmokeJobsLoadAtTheDerivedMinimum()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", RlSourceFixture.FirstDay, 1_278);
        DateOnly end = RlSourceFixture.FirstDay.AddDays(1_278);
        var shortHistory = await RlLocalSnapshotFactory.PrepareAsync(Job("ABC", RlSourceFixture.FirstDay, end) with
            { ShortHistorySpecificationId = RlShortHistoryEvaluationContract.SpecificationId }, Snapshot("short"), RlSourceFixture.Limits, CancellationToken.None);
        Assert.True(shortHistory.ShortHistoryEvaluation);
        var smoke = await RlLocalSnapshotFactory.PrepareAsync(Job("ABC", RlSourceFixture.FirstDay, end, smoke: true), Snapshot("smoke"), RlSourceFixture.Limits, CancellationToken.None);
        Assert.Empty(smoke.Catalog.Oos);
        var standard = await Assert.ThrowsAsync<InvalidDataException>(() => RlLocalSnapshotFactory.PrepareAsync(Job("ABC", RlSourceFixture.FirstDay, end),
            Snapshot("standard"), RlSourceFixture.Limits, CancellationToken.None));
        Assert.True(RlSourceContract.IsInsufficientData(standard));
    }

    [Fact]
    public async Task NonEquityIsRejected()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ETF1", RlSourceFixture.FirstDay, 1_278, quoteType: "ETF");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RlLocalSnapshotFactory.PrepareAsync(
            Job("ETF1", RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(1_278)), Snapshot(), RlSourceFixture.Limits, CancellationToken.None));
        Assert.Equal("Unavailable: T15 requires an existing local equity series.", error.Message);
    }

    [Fact]
    public async Task MissingFilesAreReported()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => RlLocalSnapshotFactory.PrepareAsync(
            Job("NONE", RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(10)), Snapshot("a"), RlSourceFixture.Limits, CancellationToken.None));
        RlSourceFixture.WriteLocalSymbol(DataRoot, "NOMETA", RlSourceFixture.FirstDay, 1_278, metadata: false);
        await Assert.ThrowsAsync<FileNotFoundException>(() => RlLocalSnapshotFactory.PrepareAsync(
            Job("NOMETA", RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(1_278)), Snapshot("b"), RlSourceFixture.Limits, CancellationToken.None));
    }

    [Fact]
    public async Task MissingMetadataFieldsAndUnknownTimezonesAreRejected()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "NOFIELD", RlSourceFixture.FirstDay, 1_278);
        RlSourceFixture.CreateParquet(Path.Combine(DataRoot, "Metadata", "NOFIELD.meta.parquet"), "SELECT 'EQUITY' AS quote_type,'UTC' AS exchange_timezone_name");
        var field = await Assert.ThrowsAsync<InvalidDataException>(() => RlLocalSnapshotFactory.PrepareAsync(
            Job("NOFIELD", RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(1_278)), Snapshot("a"), RlSourceFixture.Limits, CancellationToken.None));
        Assert.Equal("Unavailable: local metadata currency is missing; no symbol inference is permitted.", field.Message);

        RlSourceFixture.WriteLocalSymbol(DataRoot, "BADZONE", RlSourceFixture.FirstDay, 1_278, timezone: "Not/AZone");
        var zone = await Assert.ThrowsAsync<InvalidDataException>(() => RlLocalSnapshotFactory.PrepareAsync(
            Job("BADZONE", RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(1_278)), Snapshot("b"), RlSourceFixture.Limits, CancellationToken.None));
        Assert.Equal("Unavailable: stored exchange timezone is unsupported.", zone.Message);
    }

    [Fact]
    public async Task BarsOnOrAfterTheCaptureDateAreRejected()
    {
        var future = new DateOnly(2999, 1, 1);
        RlSourceFixture.WriteLocalSymbol(DataRoot, "FUTURE", future, 5);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => RlLocalSnapshotFactory.PrepareAsync(
            Job("FUTURE", future, future.AddDays(5)), Snapshot(), RlSourceFixture.Limits, CancellationToken.None));
        Assert.Equal("Unavailable: selected stored bars include the current or a future exchange date.", error.Message);
    }

    [Fact]
    public async Task CancellationIsHonored()
    {
        RlSourceFixture.WriteLocalSymbol(DataRoot, "ABC", RlSourceFixture.FirstDay, 1_278);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RlLocalSnapshotFactory.PrepareAsync(
            Job("ABC", RlSourceFixture.FirstDay, RlSourceFixture.FirstDay.AddDays(1_278)), Snapshot(), RlSourceFixture.Limits, cancelled.Token));
    }
}
