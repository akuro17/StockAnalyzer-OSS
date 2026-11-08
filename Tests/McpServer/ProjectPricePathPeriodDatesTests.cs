using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// MCP Part 3 review, T9: the future dates of a W1 or MN1 projection are calendar period starts (Monday of each week, the 1st of each month), not the last
/// bar's label plus 7 or 30 days. The fixture's market is closed on Wednesday 2024-05-01 and on the Mondays 2024-06-17 and 2024-06-24, so the last bars of
/// both frames are labelled with a first trading day that is NOT the period start - without that the check would be vacuous.
/// </summary>
public sealed class ProjectPricePathPeriodDatesTests : IDisposable
{
    private const string Symbol = "PRJPER";
    private const int Horizon = 6;
    private static readonly DateTime FirstDay = new(2021, 1, 4);
    private static readonly DateTime LastDay = new(2024, 6, 28);
    private static readonly DateTime[] Holidays = { new(2024, 5, 1), new(2024, 6, 17), new(2024, 6, 24) };

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(1);
    private readonly List<McpServerRuntime> _runtimes = new();

    public ProjectPricePathPeriodDatesTests()
    {
        var bars = new List<(DateTime Date, decimal Close)>();
        var generator = new DeterministicNoise(12345);
        int i = 0;
        for (DateTime day = FirstDay; day <= LastDay; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            double noise = generator.NextCentered();
            if (!Holidays.Contains(day))
            {
                bars.Add((day, (decimal)Math.Round(100.0 + (0.05 * i) + (8.0 * Math.Sin(i / 21.0)) + (3.0 * noise), 2)));
            }

            i++;
        }

        ParquetFixtureBuilder.SeedDailyOhlcvFromDatedCloses(_fixture.DailyDirectory, Symbol, bars);
        File.WriteAllText(_fixture.TickerListPath, JsonSerializer.Serialize(new[] { Symbol }));
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<JsonElement> CallAsync(string timeFrame, string method)
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
        using JsonDocument response = (await McpTestSupport.CallAsync(
            runtime.Dispatcher, "sa_project_price_path", $$"""{"symbol":"{{Symbol}}","timeFrame":"{{timeFrame}}","method":"{{method}}","horizonBars":{{Horizon}}}"""))!;
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).Clone();
    }

    private static DateTime Date(JsonElement element, string property)
        => DateTime.ParseExact(element.GetProperty(property).GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTime MondayOf(DateTime date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    [Theory]
    [InlineData("Arima")]
    [InlineData("Fft")]
    [InlineData("Ssa")]
    [InlineData("Hmm")]
    public async Task Monthly_FutureDates_AreTheFirstOfEachFollowingMonth(string method)
    {
        JsonElement result = await CallAsync("MN1", method);

        DateTime asOf = Date(result, "asOfDate");
        Assert.Equal(1, asOf.Day); // PeriodStart contract: monthly bars are stamped on the 1st of the month
        var firstOfAsOfMonth = new DateTime(asOf.Year, asOf.Month, 1);

        JsonElement[] path = result.GetProperty("projectedPath").EnumerateArray().ToArray();
        Assert.Equal(Horizon, path.Length);
        for (int k = 1; k <= Horizon; k++)
        {
            Assert.Equal(firstOfAsOfMonth.AddMonths(k), Date(path[k - 1], "date"));
        }
    }

    [Theory]
    [InlineData("Arima")]
    [InlineData("Fft")]
    [InlineData("Ssa")]
    [InlineData("Hmm")]
    public async Task Weekly_FutureDates_AreTheMondayOfEachFollowingWeek(string method)
    {
        JsonElement result = await CallAsync("W1", method);

        DateTime asOf = Date(result, "asOfDate");
        Assert.Equal(DayOfWeek.Monday, asOf.DayOfWeek); // PeriodStart contract: weekly bars are stamped on Monday
        DateTime monday = MondayOf(asOf);

        JsonElement[] path = result.GetProperty("projectedPath").EnumerateArray().ToArray();
        Assert.Equal(Horizon, path.Length);
        for (int k = 1; k <= Horizon; k++)
        {
            DateTime date = Date(path[k - 1], "date");
            Assert.Equal(monday.AddDays(7 * k), date);
            Assert.Equal(DayOfWeek.Monday, date.DayOfWeek);
        }
    }

    [Fact]
    public async Task Daily_FutureDates_AreUnchanged_ConsecutiveCalendarDaysFromAsOf()
    {
        JsonElement result = await CallAsync("D1", "Arima");

        DateTime asOf = Date(result, "asOfDate");
        JsonElement[] path = result.GetProperty("projectedPath").EnumerateArray().ToArray();
        for (int k = 1; k <= Horizon; k++)
        {
            Assert.Equal(asOf.AddDays(k), Date(path[k - 1], "date"));
        }
    }

    [Theory]
    [InlineData("W1", true)]
    [InlineData("MN1", true)]
    [InlineData("D1", false)]
    public async Task PeriodStartNote_IsGivenForWeeklyAndMonthlyOnly(string timeFrame, bool expected)
    {
        JsonElement result = await CallAsync(timeFrame, "Arima");

        bool hasNote = result.TryGetProperty("notes", out JsonElement notes)
            && notes.EnumerateArray().Any(n => n.GetString() == ProjectPricePathTool.PeriodStartDateNote);
        Assert.Equal(expected, hasNote);
    }
}
