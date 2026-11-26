using System.IO;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// MCP discussion optimization, Run C through the real composition: <c>excludeEvents</c>, <c>tail</c> and <c>view</c> of <c>sa_discussion_read</c>. The board itself
/// (cursor, wait, pipe) is covered by the Core tests.
/// </summary>
public sealed class DiscussionCatchUpToolTests : IDisposable
{
    private readonly DiscussionToolTestRig _rig = new("sa_discussion_catchup_");

    public void Dispose() => _rig.Dispose();

    private Task<McpServerRuntime> StartAsync(bool compactDefaults = false)
        => _rig.StartAsync(compactDefaults);

    private Task<JsonDocument> CallAsync(McpServerRuntime runtime, string tool, string argumentsJson)
        => _rig.CallAsync(runtime, tool, argumentsJson);

    /// <summary>Alice and Bob joined (events 1 and 2); Bob posted "first" (3), Carol joined (event 4), Bob posted "second" (5).</summary>
    private async Task<(string SessionId, string Alice)> OpenAsync(McpServerRuntime runtime)
    {
        using JsonDocument created = await CallAsync(runtime, "sa_discussion_create", """{"title":"catch up"}""");
        string id = McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
        string alice = await JoinAsync(runtime, id, "Alice");
        string bob = await JoinAsync(runtime, id, "Bob");
        using JsonDocument first = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}","content":"first"}""");
        await JoinAsync(runtime, id, "Carol");
        using JsonDocument second = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{id}}","participantToken":"{{bob}}","content":"second"}""");
        return (id, alice);
    }

    private Task<string> JoinAsync(McpServerRuntime runtime, string id, string name)
        => _rig.JoinAsync(runtime, id, name);

    private async Task<JsonDocument> ReadAsync(McpServerRuntime runtime, string id, string token, string extra)
        => await CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{id}}","participantToken":"{{token}}"{{extra}} }""");

    [Fact]
    public async Task TheSchemaOffersTheThreeArgumentsWithTheirBounds()
    {
        McpServerRuntime runtime = await StartAsync();

        Assert.True(runtime.Tools.TryGet("sa_discussion_read", out RegisteredTool? read));
        JsonElement properties = read!.Schema.GetProperty("properties");
        Assert.Equal("boolean", properties.GetProperty("excludeEvents").GetProperty("type").GetString());
        JsonElement tail = properties.GetProperty("tail");
        Assert.Equal(("integer", 1, 50), (tail.GetProperty("type").GetString(), tail.GetProperty("minimum").GetInt32(), tail.GetProperty("maximum").GetInt32()));
        Assert.False(tail.TryGetProperty("default", out _));
        Assert.Equal(new[] { "full", "summary" }, properties.GetProperty("view").GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        foreach (string name in new[] { "excludeEvents", "tail", "view" })
        {
            Assert.All(properties.GetProperty(name).GetProperty("description").GetString()!, ch => Assert.True(ch < 128, name));
        }

        Assert.All(read.Tool.Description, ch => Assert.True(ch < 128));
    }

    [Fact]
    public async Task ExcludeEventsTailAndSummaryWorkThroughTheTool()
    {
        McpServerRuntime runtime = await StartAsync();
        (string id, string alice) = await OpenAsync(runtime);

        using JsonDocument noEvents = await ReadAsync(runtime, id, alice, ""","excludeEvents":true""");
        using JsonDocument tail = await ReadAsync(runtime, id, alice, ""","tail":1""");
        using JsonDocument summary = await ReadAsync(runtime, id, alice, ""","tail":2,"excludeEvents":true,"view":"summary","headerMode":"none" """);

        JsonElement noEventsResult = McpTestSupport.Structured(noEvents);
        Assert.Equal(new[] { "first", "second" }, noEventsResult.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));
        Assert.Equal(5, noEventsResult.GetProperty("nextAfterSequence").GetInt32());
        Assert.Equal(new[] { "second" }, McpTestSupport.Structured(tail).GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));
        JsonElement summaryResult = McpTestSupport.Structured(summary);
        Assert.Equal(new[] { "first", "second" }, summaryResult.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("summary").GetString()));
        Assert.All(summaryResult.GetProperty("messages").EnumerateArray(), m => Assert.Equal("peer", m.GetProperty("origin").GetString()));
        Assert.False(summaryResult.TryGetProperty("session", out _));
    }

    [Theory]
    [InlineData(""","tail":0""")]
    [InlineData(""","tail":51""")]
    [InlineData(""","tail":2,"limit":5""")]
    [InlineData(""","tail":2,"afterSequence":3""")]
    [InlineData(""","tail":2,"waitSeconds":1""")]
    [InlineData(""","view":"table" """)]
    [InlineData(""","excludeEvents":"yes" """)]
    public async Task ABadCombinationIsInvalidArgumentOrLimitExceeded(string extra)
    {
        McpServerRuntime runtime = await StartAsync();
        (string id, string alice) = await OpenAsync(runtime);

        using JsonDocument refused = await ReadAsync(runtime, id, alice, extra);

        Assert.True(McpTestSupport.IsError(refused));
        Assert.Contains(McpTestSupport.ErrorCodeOf(refused), new[] { "INVALID_ARGUMENT", "LIMIT_EXCEEDED" });
    }

    [Fact]
    public async Task CompactDefaultsDoNotTurnOnTheNewArguments()
    {
        McpServerRuntime runtime = await StartAsync(compactDefaults: true);
        (string id, string alice) = await OpenAsync(runtime);

        using JsonDocument read = await ReadAsync(runtime, id, alice, "");

        JsonElement[] messages = McpTestSupport.Structured(read).GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(5, messages.Length);
        Assert.Equal("server", messages[0].GetProperty("origin").GetString());
        Assert.True(messages[0].TryGetProperty("content", out _));
    }

    [Fact]
    public async Task AWaitingFilteredReadEndsWithTimedOutOnlyAfterTheWholeWait()
    {
        McpServerRuntime runtime = await StartAsync();
        (string id, string alice) = await OpenAsync(runtime);

        using JsonDocument read = await ReadAsync(runtime, id, alice, ""","afterSequence":5,"waitSeconds":1,"excludeEvents":true""");

        JsonElement result = McpTestSupport.Structured(read);
        Assert.True(result.GetProperty("timedOut").GetBoolean());
        Assert.Equal(0, result.GetProperty("messages").GetArrayLength());
    }
}
