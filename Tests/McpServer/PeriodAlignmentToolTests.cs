using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Plan tasks T5/T6 through the tools: two markets that trade on weekdays, one of them closed on Monday 2024-01-15 (so its week of 2024-01-15 is dated
/// Tuesday 2024-01-16 by the aggregator), with W1 and D1.
/// </summary>
public sealed class PeriodAlignmentToolTests : IDisposable
{
    private const string Open = "WKA";
    private const string HolidayOnMonday = "WKB";
    private static readonly DateTime FirstDay = new(2024, 1, 1);
    private static readonly DateTime Holiday = new(2024, 1, 15);
    private static readonly DateTime LastDay = new(2024, 3, 29);

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(1);
    private readonly List<McpServerRuntime> _runtimes = new();

    public PeriodAlignmentToolTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromDatedCloses(_fixture.DailyDirectory, Open, Weekdays(includeHoliday: true, price: i => 100m + 0.9m * i + (i * i % 7)));
        ParquetFixtureBuilder.SeedDailyOhlcvFromDatedCloses(_fixture.DailyDirectory, HolidayOnMonday, Weekdays(includeHoliday: false, price: i => 60m + 0.5m * i + (i * 5 % 9)));
        File.WriteAllText(_fixture.TickerListPath, JsonSerializer.Serialize(new[] { Open, HolidayOnMonday }));
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private static List<(DateTime Date, decimal Close)> Weekdays(bool includeHoliday, Func<int, decimal> price)
    {
        var bars = new List<(DateTime, decimal)>();
        int i = 0;
        for (DateTime day = FirstDay; day <= LastDay; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            if (day != Holiday || includeHoliday)
            {
                bars.Add((day, price(i)));
            }

            i++;
        }

        return bars;
    }

    private async Task<JsonElement> CallAsync(string tool, string arguments)
    {
        var values = new Dictionary<string, string?>
        {
            ["MarketDataSettings:DailyDataPath"] = _fixture.Settings.DailyDataPath,
            ["MarketDataSettings:WeeklyDataPath"] = _fixture.Settings.WeeklyDataPath,
            ["MarketDataSettings:MonthlyDataPath"] = _fixture.Settings.MonthlyDataPath,
            ["MarketDataSettings:MetadataPath"] = _fixture.Settings.MetadataPath,
            ["MarketDataSettings:TickerListPath"] = _fixture.Settings.TickerListPath,
        };
        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()), _fixture.DailyDirectory);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        using JsonDocument response = (await McpTestSupport.CallAsync(runtime.Dispatcher, tool, arguments))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).Clone();
    }

    private static string Args(string timeFrame, string extra = "")
        => $$"""{"symbols":["{{Open}}","{{HolidayOnMonday}}"],"timeFrame":"{{timeFrame}}"{{extra}}}""";

    private static IEnumerable<string> Notes(JsonElement result) => result.GetProperty("notes").EnumerateArray().Select(n => n.GetString()!);

    [Fact]
    public async Task CrossAssetStats_W1_KeepsTheHolidayWeek_AndStatesCalendarDates()
    {
        JsonElement w1 = await CallAsync("sa_calculate_cross_asset_stats", Args("W1"));

        // 13 weeks, 12 weekly returns, all common; without the period match the week dated Tuesday by one market would drop out (11).
        Assert.Equal(12, w1.GetProperty("commonDateCount").GetInt32());
        Assert.Equal("2024-01-08", w1.GetProperty("startDate").GetString());
        Assert.Equal("2024-03-25", w1.GetProperty("endDate").GetString());
        Assert.Equal(0, w1.GetProperty("multiBarReturnCount").GetInt32());
        Assert.Contains(ReturnStatistics.PeriodStartDateNote, Notes(w1));
    }

    [Fact]
    public async Task CrossAssetStats_D1_CountsTheReturnThatCoversTwoDaysInOneMarket()
    {
        JsonElement d1 = await CallAsync("sa_calculate_cross_asset_stats", Args("D1"));

        // On 2024-01-16 market A's return covers one day (from 01-15), market B's two (from 01-12); 01-15 itself is not a common date.
        Assert.Equal(1, d1.GetProperty("multiBarReturnCount").GetInt32());
        Assert.Contains(Notes(d1), n => n.Contains("different holding periods", StringComparison.Ordinal));
        Assert.DoesNotContain(ReturnStatistics.PeriodStartDateNote, Notes(d1));
    }

    [Fact]
    public async Task MarketBreadth_W1_HasOneAxisPointPerWeek()
    {
        JsonElement w1 = await CallAsync("sa_calculate_market_breadth", Args("W1", ",\"adRatioBars\":2,\"maBars\":2,\"highLowBars\":2"));

        JsonElement dates = w1.GetProperty("series").GetProperty("dates");
        Assert.Equal(13, w1.GetProperty("dateCount").GetInt32());
        Assert.Equal(13, dates.GetArrayLength());
        foreach (JsonElement date in dates.EnumerateArray())
        {
            Assert.Equal(DayOfWeek.Monday, DateTime.Parse(date.GetString()!, CultureInfo.InvariantCulture).DayOfWeek);
        }

        Assert.Contains("2024-01-15", dates.EnumerateArray().Select(d => d.GetString()));
        Assert.DoesNotContain("2024-01-16", dates.EnumerateArray().Select(d => d.GetString()));
        Assert.Contains(MarketBreadthCalculator.AdvancersKey, w1.GetProperty("series").EnumerateObject().Select(p => p.Name));
        Assert.Contains(ReturnStatistics.PeriodStartDateNote, Notes(w1));
    }

    [Fact]
    public async Task MarketBreadth_D1_KeepsTheExactDateAxis()
    {
        JsonElement d1 = await CallAsync("sa_calculate_market_breadth", Args("D1", ",\"adRatioBars\":2,\"maBars\":2,\"highLowBars\":2"));

        // 65 weekdays in the range, one of them (2024-01-15) traded by A only: the axis is the union.
        Assert.Equal(65, d1.GetProperty("dateCount").GetInt32());
        Assert.DoesNotContain(ReturnStatistics.PeriodStartDateNote, Notes(d1));
    }
}
