using System.IO;
using System.Text;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Verifies that full MCP tool documentation lives under <c>docs/MCP/</c> in both the repository and
/// the build output directory, and that the default <c>tools/list</c> payload stays concise and ASCII-clean.
/// </summary>
public sealed class McpDocExternalizationTests
{
    private static async Task<(string ResponseJson, JsonElement Tools)> ListToolsAsync(bool enableExecution, bool enableDiscussion)
    {
        string emptyRoot = Path.Combine(Path.GetTempPath(), "sa_doc_ext_" + Guid.NewGuid().ToString("N"));
        using var runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(emptyRoot),
            new LogSink(TextWriter.Null),
            enableExecution: enableExecution,
            enableDiscussion: enableDiscussion);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        string json = (await runtime.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!;
        using JsonDocument doc = JsonDocument.Parse(json);
        return (json, doc.RootElement.GetProperty("result").GetProperty("tools").Clone());
    }

    [Fact]
    public async Task EveryToolHasNonEmptyExternalDocInRepositoryAndBuildOutputAndIsListedInReadme()
    {
        (_, JsonElement tools) = await ListToolsAsync(enableExecution: true, enableDiscussion: true);
        string repoMcpDir = Path.Combine(TestRepositoryRoot.Find(), "docs", "MCP");
        string outputMcpDir = Path.Combine(AppContext.BaseDirectory, "docs", "MCP");

        string repoReadmePath = Path.Combine(repoMcpDir, "README.md");
        string outputReadmePath = Path.Combine(outputMcpDir, "README.md");
        Assert.True(File.Exists(repoReadmePath), $"Missing repo MCP README: {repoReadmePath}");
        Assert.True(File.Exists(outputReadmePath), $"Missing output MCP README: {outputReadmePath}");
        string repoReadme = File.ReadAllText(repoReadmePath);

        int toolCount = 0;
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            toolCount++;

            Assert.Contains(name, repoReadme, StringComparison.Ordinal);

            string repoToolDoc = Path.Combine(repoMcpDir, "tools", $"{name}.md");
            string outputToolDoc = Path.Combine(outputMcpDir, "tools", $"{name}.md");
            Assert.True(File.Exists(repoToolDoc), $"Missing repo tool doc: {repoToolDoc}");
            Assert.True(File.Exists(outputToolDoc), $"Missing output tool doc: {outputToolDoc}");
            string repoDocText = File.ReadAllText(repoToolDoc);
            Assert.False(string.IsNullOrWhiteSpace(repoDocText), $"Empty repo tool doc: {repoToolDoc}");
            Assert.Contains("## Contract & Constraints", repoDocText, StringComparison.Ordinal);
            Assert.Contains("## Arguments (`inputSchema`)", repoDocText, StringComparison.Ordinal);
            Assert.Contains("## Example Call", repoDocText, StringComparison.Ordinal);
            Assert.Contains("## Common Errors", repoDocText, StringComparison.Ordinal);
        }

        Assert.True(toolCount >= 65, $"Expected at least 65 tools, found {toolCount}.");
    }

    [Fact]
    public async Task DefaultToolsListPayloadIsCompressedBelowBudgetAndAllDescriptionsAreAscii()
    {
        (string defaultJson, JsonElement defaultTools) = await ListToolsAsync(enableExecution: false, enableDiscussion: false);
        int defaultBytes = Encoding.UTF8.GetByteCount(defaultJson);
        Assert.True(defaultBytes < 112_000, $"Expected default tools/list under 112,000 bytes, got {defaultBytes} bytes.");

        int defaultDescriptionBytes = 0;
        foreach (JsonElement tool in defaultTools.EnumerateArray())
        {
            defaultDescriptionBytes += Encoding.UTF8.GetByteCount(tool.GetProperty("description").GetString()!);
        }

        Assert.True(defaultDescriptionBytes < 18_000, $"Expected default tool descriptions under 18,000 bytes, got {defaultDescriptionBytes} bytes.");

        (_, JsonElement allTools) = await ListToolsAsync(enableExecution: true, enableDiscussion: true);
        foreach (JsonElement tool in allTools.EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString()!;
            string description = tool.GetProperty("description").GetString()!;
            Assert.All(description, ch => Assert.True(ch < 128, $"Non-ASCII char U+{(int)ch:X4} in description of {name}."));
        }
    }
}
