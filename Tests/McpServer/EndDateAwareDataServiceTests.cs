using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>The loader the relative-performance tool hands to the chart's aligner: it must cut every symbol at the end date, which WindowedDataService does not.</summary>
public sealed class EndDateAwareDataServiceTests : IDisposable
{
    private const int StoredDays = 400;
    private const string Primary = "MSFT";
    private const string Other = "AAPL";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(StoredDays, Primary, Other);
    private readonly ServiceProvider _services;
    private readonly IReadOnlyStockAnalyzerGateway _gateway;
    private readonly CandleWindowLoader _loader;

    public EndDateAwareDataServiceTests()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        }).Build();
        _services = new ServiceCollection().AddStockAnalyzerReadOnlyCore(configuration).BuildServiceProvider();
        _gateway = _services.GetRequiredService<IReadOnlyStockAnalyzerGateway>();
        _loader = new CandleWindowLoader(_gateway);
    }

    public void Dispose()
    {
        _services.Dispose();
        _fixture.Dispose();
    }

    private async Task<DateOnly> DateOfBarFromEndAsync(int barsFromEnd)
    {
        IReadOnlyList<CandleData> all = await _gateway.GetCandlesAsync(Primary, TimeFrame.D1, StoredDays);
        return DateOnly.FromDateTime(all[all.Count - 1 - barsFromEnd].Timestamp);
    }

    [Fact]
    public async Task WithoutAnEndDateItLoadsTheLatestBarsLikeTheLoader()
    {
        var service = new EndDateAwareDataService(_loader, null);

        IReadOnlyList<CandleData> bars = await service.LoadCandlesAsync(Primary, TimeFrame.D1, 30);

        Assert.Equal((await _loader.LoadAsync(Primary, TimeFrame.D1, 30, CancellationToken.None)).Bars, bars);
        Assert.Equal(30, bars.Count);
    }

    [Fact]
    public async Task WithAnEndDateItLoadsTheLatestBarsOnOrBeforeThatDate()
    {
        DateOnly endDate = await DateOfBarFromEndAsync(50);
        var service = new EndDateAwareDataService(_loader, endDate);

        IReadOnlyList<CandleData> bars = await service.LoadCandlesAsync(Primary, TimeFrame.D1, 30);

        Assert.Equal(30, bars.Count);
        Assert.Equal(endDate, DateOnly.FromDateTime(bars[^1].Timestamp));
        Assert.Equal(await _gateway.GetCandlesUpToAsync(Primary, TimeFrame.D1, endDate, 30), bars);
    }

    [Fact]
    public async Task TheAlignerCutsThePrimaryAndTheComparisonSymbolAtTheEndDate()
    {
        DateOnly endDate = await DateOfBarFromEndAsync(50);

        ComparisonAlignedData cut = await new ComparisonDataAligner(new EndDateAwareDataService(_loader, endDate))
            .AlignAsync(Primary, new[] { Other }, TimeFrame.D1, 30);
        ComparisonAlignedData latest = await new ComparisonDataAligner(new EndDateAwareDataService(_loader, null))
            .AlignAsync(Primary, new[] { Other }, TimeFrame.D1, 30);

        Assert.Equal(endDate, DateOnly.FromDateTime(cut.Timestamps[^1]));
        Assert.All(cut.Series.Values, series => Assert.Equal(cut.Timestamps.Length, series.Length));
        Assert.NotNull(cut.Series[Other][^1]);
        Assert.NotEqual(cut.Timestamps[^1], latest.Timestamps[^1]);
    }

    [Fact]
    public void TheStandardComparisonTargetLimitEqualsTheAlignersOwnDefault()
    {
        var aligner = new ComparisonDataAligner(new EndDateAwareDataService(_loader, null));

        Assert.Equal(aligner.MaxComparisonSymbols, new McpLimits().MaxComparisonTargets);
    }
}
