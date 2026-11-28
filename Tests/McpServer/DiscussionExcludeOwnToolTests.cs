using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The <c>excludeOwn</c> argument of <c>sa_discussion_read</c> through the real composition: it is offered as a boolean, a value of another type is refused at the argument,
/// <c>--compact-defaults</c> never adds it, and a read that names it leaves the caller's own post out.
/// </summary>
public sealed class DiscussionExcludeOwnToolTests : IDisposable
{
    private readonly DiscussionToolTestRig _rig = new("sa_discussion_excludeown_");

    public void Dispose() => _rig.Dispose();

    private async Task<(string SessionId, string Alice, string Bob)> OpenAsync(McpServerRuntime runtime)
    {
        string sessionId = await _rig.CreateAsync(runtime, """{"title":"exclude own"}""");
        return (sessionId, await _rig.JoinAsync(runtime, sessionId, "Alice"), await _rig.JoinAsync(runtime, sessionId, "Bob"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheSchemaOffersExcludeOwnAsABooleanWithoutADefaultValue(bool compactDefaults)
    {
        McpServerRuntime runtime = await _rig.StartAsync(compactDefaults);

        Assert.True(runtime.Tools.TryGet("sa_discussion_read", out RegisteredTool? registered));
        JsonElement property = registered!.Schema.GetProperty("properties").GetProperty("excludeOwn");

        Assert.Equal("boolean", property.GetProperty("type").GetString());
        Assert.False(property.TryGetProperty("default", out _));
        Assert.Contains("Default false", property.GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AValueOfAnotherTypeIsRefusedAtTheArgument()
    {
        McpServerRuntime runtime = await _rig.StartAsync();
        (string sessionId, string alice, _) = await OpenAsync(runtime);

        using JsonDocument refused = await _rig.CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","excludeOwn":"yes"}""");

        Assert.Equal("INVALID_ARGUMENT", McpTestSupport.ErrorCodeOf(refused));
        Assert.Equal("excludeOwn", McpTestSupport.Structured(refused).GetProperty("param").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACallThatNamesItLeavesOutTheCallersOwnPostAndACallThatDoesNotKeepsIt(bool compactDefaults)
    {
        McpServerRuntime runtime = await _rig.StartAsync(compactDefaults);
        (string sessionId, string alice, string bob) = await OpenAsync(runtime);
        using JsonDocument mine = await _rig.CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","content":"my own words"}""");
        using JsonDocument theirs = await _rig.CallAsync(runtime, "sa_discussion_post", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{bob}}","content":"their words"}""");

        using JsonDocument plain = await _rig.CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","afterSequence":2,"excludeEvents":true}""");
        using JsonDocument own = await _rig.CallAsync(runtime, "sa_discussion_read", $$"""{"sessionId":"{{sessionId}}","participantToken":"{{alice}}","afterSequence":2,"excludeEvents":true,"excludeOwn":true}""");

        string[] Contents(JsonDocument read) => McpTestSupport.Structured(read).GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()!).ToArray();
        Assert.Equal(new[] { "my own words", "their words" }, Contents(plain));
        Assert.Equal(new[] { "their words" }, Contents(own));
    }
}
