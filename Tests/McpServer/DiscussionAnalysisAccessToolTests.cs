using System.IO;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// MCP discussion optimization, Run B through the real composition: <c>paths</c> of <c>sa_discussion_get_analysis</c> and <c>includeContexts</c> of <c>sa_discussion_status</c>,
/// with a real analysis tool attached.
/// </summary>
public sealed class DiscussionAnalysisAccessToolTests : IDisposable
{
    private readonly DiscussionToolTestRig _rig = new("sa_discussion_access_");

    public void Dispose() => _rig.Dispose();

    private Task<McpServerRuntime> StartAsync()
        => _rig.StartAsync();

    private Task<JsonDocument> CallAsync(McpServerRuntime runtime, string tool, string argumentsJson)
        => _rig.CallAsync(runtime, tool, argumentsJson);

    private async Task<(string SessionId, string Token)> OpenWithContextAsync(McpServerRuntime runtime)
    {
        using JsonDocument created = await CallAsync(runtime, "sa_discussion_create", """{"title":"access"}""");
        string sessionId = McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
        using JsonDocument joined = await CallAsync(runtime, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"Alice"}""");
        string token = McpTestSupport.Structured(joined).GetProperty("participantToken").GetString()!;
        using JsonDocument attached = await CallAsync(runtime, "sa_discussion_attach_analysis", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","tool":"sa_get_capabilities","analysisIntent":"CapabilityCheck"}""");
        Assert.False(McpTestSupport.IsError(attached));
        return (sessionId, token);
    }

    [Fact]
    public async Task TheSchemasOfTheTwoToolsOfferTheNewArgumentsAndTheHintPointsToPaths()
    {
        McpServerRuntime runtime = await StartAsync();

        Assert.True(runtime.Tools.TryGet("sa_discussion_get_analysis", out RegisteredTool? analysis));
        JsonElement paths = analysis!.Schema.GetProperty("properties").GetProperty("paths");
        Assert.Equal("array", paths.GetProperty("type").GetString());
        Assert.Equal("string", paths.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(1, paths.GetProperty("minItems").GetInt32());
        Assert.All(paths.GetProperty("description").GetString()!, ch => Assert.True(ch < 128));
        Assert.Contains("includeResult", analysis.Tool.CompactUsageHint, StringComparison.Ordinal);
        Assert.Contains("paths", analysis.Tool.CompactUsageHint, StringComparison.Ordinal);

        Assert.True(runtime.Tools.TryGet("sa_discussion_status", out RegisteredTool? status));
        JsonElement includeContexts = status!.Schema.GetProperty("properties").GetProperty("includeContexts");
        Assert.Equal("boolean", includeContexts.GetProperty("type").GetString());
        Assert.All(includeContexts.GetProperty("description").GetString()!, ch => Assert.True(ch < 128));
        Assert.False(analysis.Schema.GetProperty("required").EnumerateArray().Any(r => r.GetString() == "paths"));
    }

    [Fact]
    public async Task PathsReturnTheNamedFieldsOfARealAttachedResult()
    {
        McpServerRuntime runtime = await StartAsync();
        (string sessionId, string token) = await OpenWithContextAsync(runtime);

        using JsonDocument whole = await CallAsync(runtime, "sa_discussion_get_analysis", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","contextId":1}""");
        JsonElement full = McpTestSupport.Structured(whole);
        using JsonDocument picked = await CallAsync(runtime, "sa_discussion_get_analysis", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","contextId":1,"paths":["periodUnit","indicatorCount","supportedTimeFrames[0]","nothing.here"]}""");
        JsonElement result = McpTestSupport.Structured(picked);

        Assert.False(result.TryGetProperty("result", out _));
        JsonElement extracted = result.GetProperty("extracted");
        Assert.Equal(full.GetProperty("result").GetProperty("periodUnit").GetRawText(), extracted.GetProperty("periodUnit").GetRawText());
        Assert.Equal(full.GetProperty("result").GetProperty("indicatorCount").GetRawText(), extracted.GetProperty("indicatorCount").GetRawText());
        Assert.Equal(full.GetProperty("result").GetProperty("supportedTimeFrames")[0].GetRawText(), extracted.GetProperty("supportedTimeFrames[0]").GetRawText());
        Assert.Equal(new[] { "nothing.here" }, result.GetProperty("missingPaths").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(full.GetProperty("sha256").GetString(), result.GetProperty("sha256").GetString());
        Assert.True(picked.RootElement.GetRawText().Length < whole.RootElement.GetRawText().Length);
    }

    [Fact]
    public async Task APathThatBreaksTheGrammarAndPathsWithIncludeResultFalseAreInvalidArgument()
    {
        McpServerRuntime runtime = await StartAsync();
        (string sessionId, string token) = await OpenWithContextAsync(runtime);

        using JsonDocument bad = await CallAsync(runtime, "sa_discussion_get_analysis", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","contextId":1,"paths":["a b"]}""");
        using JsonDocument empty = await CallAsync(runtime, "sa_discussion_get_analysis", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","contextId":1,"paths":[]}""");
        using JsonDocument conflict = await CallAsync(runtime, "sa_discussion_get_analysis", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{token}}","contextId":1,"paths":["periodUnit"],"includeResult":false}""");

        Assert.All(new[] { bad, empty, conflict }, response => Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(response)));
    }

    [Fact]
    public async Task IncludeContextsListsTheAttachedContextAndNeedsASessionId()
    {
        McpServerRuntime runtime = await StartAsync();
        (string sessionId, _) = await OpenWithContextAsync(runtime);

        using JsonDocument listed = await CallAsync(runtime, "sa_discussion_status", $$"""{"sessionId":"{{sessionId}}","includeContexts":true}""");
        JsonElement context = McpTestSupport.Structured(listed).GetProperty("session").GetProperty("contexts")[0];
        using JsonDocument withoutSession = await CallAsync(runtime, "sa_discussion_status", """{"includeContexts":true}""");

        Assert.Equal("sa_get_capabilities", context.GetProperty("tool").GetString());
        Assert.Equal("CapabilityCheck", context.GetProperty("analysisIntent").GetString());
        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(withoutSession));
    }
}
