using System.IO;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// MCP discussion optimization, Run A: <c>compact</c> and <c>headerMode</c> of <c>sa_discussion_read</c> and the <c>--compact-defaults</c> wiring through the real composition.
/// A value the caller sends is never replaced; without the flag nothing is added.
/// </summary>
public sealed class DiscussionCompactDefaultsTests : IDisposable
{
    private readonly DiscussionToolTestRig _rig = new("sa_discussion_compact_");

    public void Dispose() => _rig.Dispose();

    private Task<McpServerRuntime> StartAsync(bool compactDefaults)
        => _rig.StartAsync(compactDefaults);

    private Task<JsonDocument> CallAsync(McpServerRuntime runtime, string tool, string argumentsJson)
        => _rig.CallAsync(runtime, tool, argumentsJson);

    private async Task<(string SessionId, string Token)> OpenAsync(McpServerRuntime runtime)
    {
        using JsonDocument created = await CallAsync(runtime, "sa_discussion_create", """{"title":"compact defaults","topic":"a topic"}""");
        string sessionId = McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
        using JsonDocument joined = await CallAsync(runtime, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Alice"}""");
        return (sessionId, McpTestSupport.Structured(joined).GetProperty("participantToken").GetString()!);
    }

    private async Task<JsonElement> ReadAsync(McpServerRuntime runtime, string sessionId, string token, string extra)
    {
        using JsonDocument read = await CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}"{{extra}} }""");
        return McpTestSupport.Structured(read).Clone();
    }

    private static JsonElement ReadSchema(McpServerRuntime runtime)
    {
        Assert.True(runtime.Tools.TryGet("sa_discussion_read", out RegisteredTool? registered));
        return registered!.Schema.GetProperty("properties");
    }

    [Theory]
    [InlineData(false, "false", "full")]
    [InlineData(true, "true", "minimal")]
    public async Task TheSchemaOfReadOffersBothArgumentsAndStatesTheDefaultOfTheFlag(bool compactDefaults, string compactDefault, string headerDefault)
    {
        McpServerRuntime runtime = await StartAsync(compactDefaults);

        JsonElement properties = ReadSchema(runtime);

        JsonElement compact = properties.GetProperty("compact");
        Assert.Equal("boolean", compact.GetProperty("type").GetString());
        Assert.EndsWith($"Default {compactDefault}.", compact.GetProperty("description").GetString(), StringComparison.Ordinal);
        JsonElement headerMode = properties.GetProperty("headerMode");
        Assert.Equal(new[] { "full", "minimal", "none" }, headerMode.GetProperty("enum").EnumerateArray().Select(v => v.GetString()));
        Assert.EndsWith($"Default {headerDefault}.", headerMode.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.All(new[] { compact, headerMode }, property => Assert.All(property.GetProperty("description").GetString()!, ch => Assert.True(ch < 128)));
        Assert.False(runtime.Tools.TryGet("sa_discussion_post", out RegisteredTool? post) && post!.Schema.GetProperty("properties").TryGetProperty("compact", out _));
    }

    [Fact]
    public async Task WithoutTheFlagAReadIsTheFullFormAndTheOtherEightToolsHaveNoFormattingArguments()
    {
        McpServerRuntime runtime = await StartAsync(compactDefaults: false);
        (string sessionId, string token) = await OpenAsync(runtime);

        JsonElement result = await ReadAsync(runtime, sessionId, token, "");

        Assert.True(result.TryGetProperty("timedOut", out _));
        Assert.Equal("a topic", result.GetProperty("session").GetProperty("topic").GetString());
        Assert.True(result.GetProperty("messages")[0].TryGetProperty("replyToSequence", out _));
        foreach (RegisteredTool registered in runtime.Tools.Tools.Where(t => t.Tool.Name.StartsWith("sa_discussion_", StringComparison.Ordinal) && t.Tool.Name != "sa_discussion_read"))
        {
            JsonElement properties = registered.Schema.GetProperty("properties");
            Assert.False(properties.TryGetProperty("compact", out _), registered.Tool.Name);
            Assert.False(properties.TryGetProperty("headerMode", out _), registered.Tool.Name);
        }
    }

    [Fact]
    public async Task WithTheFlagAnOmittedOrNullArgumentIsFilledIn()
    {
        McpServerRuntime runtime = await StartAsync(compactDefaults: true);
        (string sessionId, string token) = await OpenAsync(runtime);

        foreach (string extra in new[] { "", ""","compact":null""", ""","headerMode":null""", ""","compact":null,"headerMode":null""" })
        {
            JsonElement result = await ReadAsync(runtime, sessionId, token, extra);

            Assert.False(result.TryGetProperty("timedOut", out _), extra);
            Assert.False(result.GetProperty("messages")[0].TryGetProperty("replyToSequence", out _), extra);
            Assert.Equal(
                new[] { "id", "state", "messageCount", "remainingMessages", "contextCount" },
                result.GetProperty("session").EnumerateObject().Select(p => p.Name));
        }
    }

    [Fact]
    public async Task WithTheFlagAValueTheCallerSendsIsNeverReplaced()
    {
        McpServerRuntime runtime = await StartAsync(compactDefaults: true);
        (string sessionId, string token) = await OpenAsync(runtime);

        JsonElement explicitLegacy = await ReadAsync(runtime, sessionId, token, ""","compact":false,"headerMode":"full" """);
        Assert.True(explicitLegacy.TryGetProperty("timedOut", out _));
        Assert.True(explicitLegacy.GetProperty("messages")[0].TryGetProperty("replyToSequence", out _));
        Assert.Equal("a topic", explicitLegacy.GetProperty("session").GetProperty("topic").GetString());

        JsonElement onlyCompactFalse = await ReadAsync(runtime, sessionId, token, ""","compact":false""");
        Assert.True(onlyCompactFalse.TryGetProperty("timedOut", out _));
        Assert.Equal(new[] { "id", "state", "floor", "messageCount", "remainingMessages", "contextCount" }, onlyCompactFalse.GetProperty("session").EnumerateObject().Select(p => p.Name));

        JsonElement onlyFull = await ReadAsync(runtime, sessionId, token, ""","headerMode":"full" """);
        Assert.False(onlyFull.TryGetProperty("timedOut", out _));
        Assert.Equal("a topic", onlyFull.GetProperty("session").GetProperty("topic").GetString());

        JsonElement none = await ReadAsync(runtime, sessionId, token, ""","headerMode":"none" """);
        Assert.False(none.TryGetProperty("session", out _));
        Assert.False(none.TryGetProperty("timedOut", out _));
    }

    [Fact]
    public async Task OtherArgumentsOfTheCallAreForwardedUnchangedWhenDefaultsAreAdded()
    {
        McpServerRuntime runtime = await StartAsync(compactDefaults: true);
        (string sessionId, string token) = await OpenAsync(runtime);
        using JsonDocument posted = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","content":"second entry"}""");
        Assert.Equal(2, McpTestSupport.Structured(posted).GetProperty("sequence").GetInt32());

        JsonElement result = await ReadAsync(runtime, sessionId, token, ""","afterSequence":1,"limit":1""");

        JsonElement message = Assert.Single(result.GetProperty("messages").EnumerateArray());
        Assert.Equal(2, message.GetProperty("sequence").GetInt32());
        Assert.Equal("second entry", message.GetProperty("content").GetString());
        Assert.Equal(2, result.GetProperty("nextAfterSequence").GetInt32());
        Assert.False(result.GetProperty("hasMore").GetBoolean());
    }

    [Fact]
    public async Task AnInvalidHeaderModeIsRefusedByTheSchemaOfTheTool()
    {
        McpServerRuntime runtime = await StartAsync(compactDefaults: false);
        (string sessionId, string token) = await OpenAsync(runtime);

        using JsonDocument refused = await CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","headerMode":"short"}""");

        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(refused));
    }

    [Fact]
    public async Task AnInterpretationOnlyPostWorksThroughTheTool()
    {
        McpServerRuntime runtime = await StartAsync(compactDefaults: false);
        (string sessionId, string token) = await OpenAsync(runtime);

        using JsonDocument posted = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","interpretation":{"summary":"up","direction":"Bullish"} }""");
        Assert.True(McpTestSupport.Structured(posted).GetProperty("hasInterpretation").GetBoolean());

        using JsonDocument blank = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","content":"","interpretation":{"summary":"up"} }""");
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(blank));

        using JsonDocument none = await CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}"}""");
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(none));
    }
}
