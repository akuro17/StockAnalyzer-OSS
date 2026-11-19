using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Tests.TestHelpers;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T7 (decision D-7): CrossLine and DatePriceRange in sa_calculate_drawing_values. The value parity with the GUI objects for every catalog tool (including these two)
/// is in <see cref="CalculateDrawingValuesToolTests"/>; this class pins what is specific to the two: the schema and description, the keys, and the bar-based range figures.
/// </summary>
public sealed class CalculateDrawingValuesCrossLineDatePriceRangeTests : IDisposable
{
    private const int Days = 100;
    private const string Symbol = "DVTEST";
    private static readonly DateTime FirstDay = new(2024, 1, 1);

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(Days, "msft", "dvtest");
    private readonly List<McpServerRuntime> _runtimes = new();
    private readonly List<decimal> _closes = Enumerable.Range(0, Days).Select(i => 100m + Math.Round((decimal)Math.Sin(i / 5.0) * 10m, 2)).ToList();

    public CalculateDrawingValuesCrossLineDatePriceRangeTests()
    {
        ParquetFixtureBuilder.SeedDailyOhlcvFromCloses(_fixture.DailyDirectory, Symbol, _closes);
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync()
    {
        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(McpTestSupport.DataTreeConfiguration(_fixture)).Build(), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> CallAsync(McpMessageDispatcher dispatcher, string tool, string anchorPart)
        => (await McpTestSupport.CallAsync(dispatcher, "sa_calculate_drawing_values", $$"""{"symbol":"{{Symbol}}","timeFrame":"D1","tool":"{{tool}}",{{anchorPart}}}"""))!;

    private static JsonElement[] ValuesOf(JsonDocument response)
    {
        Assert.False(McpTestSupport.IsError(response), McpTestSupport.Structured(response).ToString());
        return McpTestSupport.Structured(response).GetProperty("evaluations")[0].GetProperty("values").EnumerateArray().ToArray();
    }

    private static string DateText(int dayIndex) => FirstDay.AddDays(dayIndex).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheToolEnumAndTheDescriptionNameBothToolsAfterTheExistingOnes()
    {
        var dispatcher = await StartAsync();

        string response = (await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!;
        using JsonDocument listing = JsonDocument.Parse(response);
        JsonElement tool = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "sa_calculate_drawing_values");
        string[] names = tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("tool").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray();

        Assert.Equal(DrawingValueCatalog.All.Select(d => d.Name).ToArray(), names);
        Assert.Equal(26, names.Length);
        Assert.Equal(new[] { "ClassicPivotPoints", "CrossLine", "DatePriceRange" }, names[^3..]);
        string description = tool.GetProperty("description").GetString()!;
        Assert.Contains("DatePriceRange", description);
        Assert.Contains("not annualised", description);
        Assert.Contains("CrossLine", description);
        Assert.Contains("CrossLine (1 point)", tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("tool").GetProperty("description").GetString());
        Assert.Contains("DatePriceRange (2 points)", tool.GetProperty("inputSchema").GetProperty("properties").GetProperty("tool").GetProperty("description").GetString());
    }

    [Fact]
    public async Task CrossLineGivesTheHorizontalLinesValuesFollowedByTheVerticalLinesWithoutAKeyCollision()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(dispatcher, "CrossLine", "\"anchorMode\":\"RangeEndpoints\",\"anchorBars\":10");

        JsonElement[] values = ValuesOf(response);
        string[] keys = values.Select(v => v.GetProperty("key").GetString()!).ToArray();
        Assert.Contains("Price", keys);
        Assert.Contains("LineDate", keys);
        Assert.Contains("Offset", keys);
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(1, McpTestSupport.Structured(response).GetProperty("controlPoints").GetArrayLength());
    }

    [Fact]
    public async Task DatePriceRangeGivesTheTenFiguresOverTheBarsBetweenItsTwoDates()
    {
        var dispatcher = await StartAsync();

        using JsonDocument response = await CallAsync(
            dispatcher,
            "DatePriceRange",
            $"\"anchorMode\":\"Explicit\",\"points\":[{{\"date\":\"{DateText(10)}\",\"price\":100}},{{\"date\":\"{DateText(60)}\",\"price\":112}}]");

        JsonElement[] values = ValuesOf(response);
        Assert.Equal(DatePriceRangeValues.ValueCount, values.Length);
        Assert.Equal(
            new[] { "PriceDelta", "PriceChangePercent", "BarCount", "CalendarDays", "TotalVolume", "AverageVolume", "HighestHigh", "LowestLow", "MaxDrawdown", "Volatility" },
            values.Select(v => v.GetProperty("key").GetString()).ToArray());
        Assert.Equal(12m, values[0].GetProperty("value").GetDecimal());
        Assert.Equal(51m, values[2].GetProperty("value").GetDecimal()); // BarCount: the inclusive range of daily bars
        Assert.Equal(50m, values[3].GetProperty("value").GetDecimal()); // CalendarDays
        Assert.Equal(51000m, values[4].GetProperty("value").GetDecimal()); // TotalVolume: 51 bars of 1000
        Assert.Equal(_closes.Skip(10).Take(51).Max() + 0.5m, values[DatePriceRangeValues.HighestHighIndex].GetProperty("value").GetDecimal());
        Assert.Equal(_closes.Skip(10).Take(51).Min() - 0.5m, values[DatePriceRangeValues.LowestLowIndex].GetProperty("value").GetDecimal());
    }

    [Fact]
    public async Task TheRangeExtremesAnchorsGiveABarCountAndAVolatilityFromTheLoadedBars()
    {
        var dispatcher = await StartAsync();

        using JsonDocument withBars = await CallAsync(dispatcher, "DatePriceRange", "\"anchorMode\":\"RangeExtremes\",\"anchorBars\":40");

        JsonElement[] values = ValuesOf(withBars);
        Assert.True(values[2].GetProperty("value").GetDecimal() > 0m); // BarCount
        Assert.NotEqual(JsonValueKind.Null, values[DatePriceRangeValues.VolatilityIndex].GetProperty("value").ValueKind);
    }
}
