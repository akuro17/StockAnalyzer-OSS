using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// sa_get_candles reads the stored daily file twice for a W1 or MN1 call (the bars, and the last stored day for referenceDate). The application's Sync can
/// replace the file between the two reads. referenceDate must come from the read made FIRST, so it is never later than the data behind the bars and the
/// partial flag can only err towards "partial". Here the file grows from Wednesday 2024-01-10 to Monday 2024-01-15 right after the first read of the call.
/// </summary>
public sealed class GetCandlesReferenceDateOrderTests : IDisposable
{
    private const string Symbol = "SYNC";
    private const int DaysBeforeSync = 10; // 2024-01-01 (Monday) .. 2024-01-10 (Wednesday)
    private const int DaysAfterSync = 15;  // .. 2024-01-15 (the Monday of the next week)

    private readonly ReadOnlyDataTreeFixture _fixture;
    private readonly ServiceProvider _services;
    private readonly GetCandlesTool _tool;

    public GetCandlesReferenceDateOrderTests()
    {
        _fixture = ReadOnlyDataTreeFixture.Create(DaysBeforeSync, Symbol);
        File.Delete(Path.Combine(_fixture.DailyDirectory, $"{Symbol}.parquet"));
        Seed(DaysBeforeSync);

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        }).Build();
        var services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration);
        services.Replace(ServiceDescriptor.Singleton<IDataService>(sp => new SyncAfterFirstDailyReadDataService(
            new ParquetDataService(sp.GetRequiredService<DuckDBConnectionManager>(), sp.GetRequiredService<IOptions<MarketDataSettings>>()),
            () => Seed(DaysAfterSync))));
        _services = services.BuildServiceProvider();

        var gateway = _services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
        _tool = new GetCandlesTool(gateway, new SymbolResolver(gateway), new McpLimits());
    }

    public void Dispose()
    {
        _services.Dispose();
        _fixture.Dispose();
    }

    private void Seed(int days)
        => StockAnalyzer.Core.Tests.TestHelpers.ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(
            _fixture.DailyDirectory, Symbol, Enumerable.Range(0, days).Select(i => 100m + i).ToList());

    [Theory]
    [InlineData("""{"symbol":"SYNC","timeFrame":"W1","count":2}""")]
    [InlineData("""{"symbol":"SYNC","timeFrame":"W1","count":2,"endDate":"2024-01-31"}""")]
    public async Task ReferenceDateIsReadBeforeTheBarsSoASyncBetweenTheReadsCannotMakeAPartialWeekLookComplete(string argumentsJson)
    {
        using JsonDocument arguments = JsonDocument.Parse(argumentsJson);

        using JsonDocument result = JsonDocument.Parse(await _tool.InvokeAsync(arguments.RootElement, CancellationToken.None));

        JsonElement root = result.RootElement;
        Assert.Equal("2024-01-10", root.GetProperty("referenceDate").GetString()); // the file as it was at the first read
        Assert.True(root.GetProperty("lastBarPartial").GetBoolean());              // the newest bar is a week that ends on a Sunday after Wednesday the 10th
    }

    /// <summary>Runs <paramref name="afterFirstDailyRead"/> once, right after the first daily load of the service has returned.</summary>
    private sealed class SyncAfterFirstDailyReadDataService(IDataService inner, Action afterFirstDailyRead) : IDataService
    {
        private int _dailyReads;

        public async Task<IReadOnlyList<CandleData>> LoadCandlesAsync(string symbol, TimeFrame timeFrame, int count = 100)
        {
            IReadOnlyList<CandleData> bars = await inner.LoadCandlesAsync(symbol, timeFrame, count);
            if (timeFrame == TimeFrame.D1 && Interlocked.Increment(ref _dailyReads) == 1)
            {
                afterFirstDailyRead();
            }

            return bars;
        }
    }
}
