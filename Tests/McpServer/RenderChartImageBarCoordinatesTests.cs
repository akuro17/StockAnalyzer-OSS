using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// WebAI gap review T3 (decision D-2c/D-2d): <c>includeBarCoordinates</c> of sa_render_chart_image. true (and omitted) keeps the answer; false leaves out the bars list and its
/// units text and nothing else: the counts, the axes, the panels, the legend and the picture are the same.
/// </summary>
public sealed class RenderChartImageBarCoordinatesTests : IDisposable
{
    private const string Frame = "\"symbol\":\"MSFT\",\"timeFrame\":\"D1\",\"barCount\":30";

    private readonly ReadOnlyDataTreeFixture _fixture = ReadOnlyDataTreeFixture.Create(400, "msft");
    private readonly List<McpServerRuntime> _runtimes = new();

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        _fixture.Dispose();
    }

    private async Task<McpMessageDispatcher> StartAsync(params (string Key, string Value)[] limitOverrides)
    {
        Dictionary<string, string?> values = McpTestSupport.DataTreeConfiguration(_fixture);
        foreach ((string key, string value) in limitOverrides)
        {
            values[$"AiIntegration:{key}"] = value;
        }

        McpServerRuntime runtime = McpServerComposition.Create(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new LogSink(new StringWriter()));
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime.Dispatcher;
    }

    private static async Task<JsonDocument> RenderAsync(McpMessageDispatcher dispatcher, string extra = "")
        => (await McpTestSupport.CallAsync(dispatcher, "sa_render_chart_image", $"{{{Frame}{extra}}}"))!;

    private static string PngOf(JsonDocument response) => McpTestSupport.Result(response).GetProperty("content")[1].GetProperty("data").GetString()!;

    private static string[] Keys(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public async Task OmittedAndTrueGiveTheSameBytesAndTheBarsListWithItsUnitsText()
    {
        var dispatcher = await StartAsync();

        using var omitted = await RenderAsync(dispatcher);
        using var explicitTrue = await RenderAsync(dispatcher, ",\"includeBarCoordinates\":true");

        JsonElement result = McpTestSupport.Structured(omitted);
        Assert.Equal(result.GetRawText(), McpTestSupport.Structured(explicitTrue).GetRawText());
        Assert.Equal(PngOf(omitted), PngOf(explicitTrue));
        Assert.Equal(30, result.GetProperty("bars").GetArrayLength());
        Assert.True(result.GetProperty("units").TryGetProperty("bars", out _));
        Assert.Equal(new[] { "pixels", "prices", "bars", "dates" }, Keys(result.GetProperty("units")));
    }

    [Fact]
    public async Task FalseLeavesOutOnlyTheBarsListAndTheSentenceOnItAndTheRestIsIdentical()
    {
        var dispatcher = await StartAsync();

        using var withBars = await RenderAsync(dispatcher);
        using var without = await RenderAsync(dispatcher, ",\"includeBarCoordinates\":false");

        JsonElement full = McpTestSupport.Structured(withBars);
        JsonElement slim = McpTestSupport.Structured(without);
        Assert.False(McpTestSupport.IsError(without), slim.GetRawText());
        Assert.Equal(Keys(full).Where(k => k != "bars"), Keys(slim));
        foreach (string key in Keys(slim).Where(k => k != "units"))
        {
            Assert.Equal(full.GetProperty(key).GetRawText(), slim.GetProperty(key).GetRawText());
        }

        // The keys of units are the same; units.bars keeps only the sentence on the three counts (they are written in both modes), every other unit is identical.
        Assert.Equal(Keys(full.GetProperty("units")), Keys(slim.GetProperty("units")));
        foreach (string key in Keys(slim.GetProperty("units")).Where(k => k != "bars"))
        {
            Assert.Equal(full.GetProperty("units").GetProperty(key).GetRawText(), slim.GetProperty("units").GetProperty(key).GetRawText());
        }

        const string CountsSentence = "barCountDrawn, barsOmittedCount and legendEntriesInImage are numbers of bars or entries.";
        string fullBarsUnit = full.GetProperty("units").GetProperty("bars").GetString()!;
        string slimBarsUnit = slim.GetProperty("units").GetProperty("bars").GetString()!;
        Assert.Equal(CountsSentence, slimBarsUnit);
        Assert.EndsWith(CountsSentence, fullBarsUnit, StringComparison.Ordinal);
        Assert.StartsWith("index counts bars from 0", fullBarsUnit, StringComparison.Ordinal);
        Assert.DoesNotContain("index counts bars", slimBarsUnit, StringComparison.Ordinal);
        Assert.True(slim.TryGetProperty("barCountDrawn", out _) && slim.TryGetProperty("barsOmittedCount", out _) && slim.TryGetProperty("legendEntriesInImage", out _), "the three counts the sentence explains are still written");

        Assert.Equal(PngOf(withBars), PngOf(without));
        Assert.True(McpTestSupport.Result(without).GetProperty("content")[0].GetProperty("text").GetString()!.Length < McpTestSupport.Result(withBars).GetProperty("content")[0].GetProperty("text").GetString()!.Length);
        Assert.Equal(McpTestSupport.Result(without).GetProperty("content")[0].GetProperty("text").GetString(), slim.GetRawText());
    }

    [Fact]
    public async Task TheCountsAndTheMappingStayWhenTheBarsAreOmitted()
    {
        var dispatcher = await StartAsync(("MaxImageBarsListed", "10"));

        using var without = await RenderAsync(dispatcher, ",\"includeBarCoordinates\":false");
        using var withBars = await RenderAsync(dispatcher);

        JsonElement slim = McpTestSupport.Structured(without);
        Assert.Equal(30, slim.GetProperty("barCountDrawn").GetInt32());
        Assert.Equal(20, slim.GetProperty("barsOmittedCount").GetInt32());
        Assert.Equal(10, McpTestSupport.Structured(withBars).GetProperty("bars").GetArrayLength());
        Assert.Equal(McpTestSupport.Structured(withBars).GetProperty("barsOmittedCount").GetInt32(), slim.GetProperty("barsOmittedCount").GetInt32());
        Assert.True(slim.TryGetProperty("axes", out _));
        Assert.True(slim.TryGetProperty("panels", out _));
        Assert.True(slim.TryGetProperty("legend", out _));
        Assert.True(slim.TryGetProperty("markers", out _));
    }

    [Fact]
    public async Task ANonBooleanValueIsRefused()
    {
        var dispatcher = await StartAsync();

        using var response = await RenderAsync(dispatcher, ",\"includeBarCoordinates\":\"no\"");

        Assert.True(McpTestSupport.IsError(response));
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response));
    }

    [Fact]
    public async Task TheSchemaAdvertisesABooleanWithoutADefaultKeyChange()
    {
        var dispatcher = await StartAsync();

        string response = (await dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!;
        using JsonDocument listing = JsonDocument.Parse(response);
        JsonElement property = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "sa_render_chart_image")
            .GetProperty("inputSchema").GetProperty("properties").GetProperty("includeBarCoordinates");

        Assert.Equal("boolean", property.GetProperty("type").GetString());
        Assert.Contains("Default true", property.GetProperty("description").GetString());
    }
}
