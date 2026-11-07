using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;
using StockAnalyzer.TestSupport;
using Xunit;
using static StockAnalyzer.Core.Tests.Services.ParquetDataServiceLoadCharacterizationTests;

namespace StockAnalyzer.Core.Tests.Services;

/// <summary>The three outcomes of <see cref="IDataReadStatusSource.LoadCandlesWithStatusAsync"/> and their agreement with the plain load.</summary>
public sealed class ParquetDataServiceReadStatusTests : IDisposable
{
    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, Msft);
    private readonly DuckDBConnectionManager _db = new();
    private readonly RecordingLogger _log = new();

    public void Dispose()
    {
        _db.Dispose();
        _fixture.Dispose();
    }

    private ParquetDataService Service() => new(_db, Options.Create(_fixture.Settings), _log);

    private string DailyFile => Path.Combine(_fixture.DailyDirectory, Msft + ".parquet");

    [Theory]
    [InlineData(Kind.Valid, DataReadStatus.Read, Days, 0)]
    [InlineData(Kind.Missing, DataReadStatus.FileMissing, 0, 0)]
    [InlineData(Kind.Corrupt, DataReadStatus.Unreadable, 0, 1)]
    [InlineData(Kind.ZeroBytes, DataReadStatus.Unreadable, 0, 1)]
    [InlineData(Kind.HeaderOnly, DataReadStatus.Read, 0, 0)]   // a file with the columns and no row was read: no bar, not unreadable
    public async Task EachKindOfDailyFileHasItsStatus_AndAnUnreadableOneIsLoggedOnce(Kind kind, DataReadStatus expected, int bars, int errors)
    {
        Prepare(kind, DailyFile);

        DataReadResult result = await Service().LoadCandlesWithStatusAsync(Msft, TimeFrame.D1, 0);

        Assert.Equal(expected, result.Status);
        Assert.Equal(bars, result.Candles.Count);
        Assert.Equal(errors, _log.Errors.Count);
    }

    [Theory]
    [InlineData(Kind.Valid)]
    [InlineData(Kind.Missing)]
    [InlineData(Kind.Corrupt)]
    [InlineData(Kind.ZeroBytes)]
    [InlineData(Kind.HeaderOnly)]
    public async Task TheStatusMethodAndThePlainLoadReturnTheSameBars(Kind kind)
    {
        Prepare(kind, DailyFile);

        DataReadResult withStatus = await Service().LoadCandlesWithStatusAsync(Msft, TimeFrame.D1, 0);
        IReadOnlyList<CandleData> plain = await Service().LoadCandlesAsync(Msft, TimeFrame.D1, 0);

        Assert.Equal(plain, withStatus.Candles);
    }

    [Fact]
    public async Task CancellationIsRethrownNeverReportedAsUnreadable()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().LoadCandlesWithStatusAsync(Msft, TimeFrame.D1, 0, cancelled.Token));
        Assert.Empty(_log.Errors);
    }

    [Fact]
    public async Task ACountLimitsTheBarsToTheLatestOnes()
    {
        DataReadResult all = await Service().LoadCandlesWithStatusAsync(Msft, TimeFrame.D1, 0);
        DataReadResult latest = await Service().LoadCandlesWithStatusAsync(Msft, TimeFrame.D1, 5);

        Assert.Equal(all.Candles.Skip(Days - 5).ToList(), latest.Candles);
    }

    [Fact]
    public async Task AFrameThatIsNotStoredAsParquetStillThrows()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => Service().LoadCandlesWithStatusAsync(Msft, TimeFrame.H1, 0));
    }
}
