using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using StockAnalyzer.McpServer;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The tools the server lists (the default set plus the execution tools) and the tool sections of <c>docs/MCP_SERVER_COMMAND_REFERENCE.md</c>
/// are the same set of names. The check is about names only: it cannot say that a section describes its tool correctly.
/// </summary>
public sealed class McpToolDocumentParityTests
{
    private static readonly Regex Heading = new(@"^###\s+\d+\.\d+\s+`(sa_[a-z0-9_]+)`\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

    internal static IReadOnlyList<string> DocumentedToolNames(string markdown)
        => Heading.Matches(markdown).Select(m => m.Groups[1].Value).ToList();

    private static async Task<IReadOnlyList<string>> ListedToolNamesAsync(bool enableExecution, bool enableDiscussion = false)
    {
        string emptyRoot = Path.Combine(Path.GetTempPath(), "sa_tool_names_" + Guid.NewGuid().ToString("N"));
        using var runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(emptyRoot),
            new LogSink(TextWriter.Null),
            enableExecution: enableExecution,
            enableDiscussion: enableDiscussion);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        string json = await runtime.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", CancellationToken.None);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    [Fact]
    public async Task EveryListedToolHasExactlyOneSectionInTheCommandReferenceAndNoSectionNamesAnUnknownTool()
    {
        string path = Path.Combine(TestRepositoryRoot.Find(), "docs", "MCP_SERVER_COMMAND_REFERENCE.md");
        Assert.True(File.Exists(path), $"Documentation file not found: {path}");
        IReadOnlyList<string> documented = DocumentedToolNames(File.ReadAllText(path));
        IReadOnlyList<string> listed = await ListedToolNamesAsync(enableExecution: true, enableDiscussion: true);

        Assert.Equal(documented.Count, documented.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(listed.Except(documented, StringComparer.Ordinal));
        Assert.Empty(documented.Except(listed, StringComparer.Ordinal));
        Assert.NotEmpty(listed);
    }

    [Fact]
    public async Task TheExecutionToolsAreTheOnlyDifferenceBetweenTheTwoListings()
    {
        IReadOnlyList<string> defaults = await ListedToolNamesAsync(enableExecution: false);
        IReadOnlyList<string> all = await ListedToolNamesAsync(enableExecution: true);

        Assert.True(all.Count > defaults.Count);
        Assert.Empty(defaults.Except(all, StringComparer.Ordinal));
    }

    [Fact]
    public async Task TheDiscussionToolsAreTheOnlyDifferenceTheirFlagMakes()
    {
        IReadOnlyList<string> without = await ListedToolNamesAsync(enableExecution: false);
        IReadOnlyList<string> with = await ListedToolNamesAsync(enableExecution: false, enableDiscussion: true);

        Assert.Empty(without.Except(with, StringComparer.Ordinal));
        Assert.Equal(
            new[]
            {
                "sa_discussion_attach_analysis", "sa_discussion_close", "sa_discussion_create", "sa_discussion_get_analysis", "sa_discussion_join",
                "sa_discussion_leave", "sa_discussion_post", "sa_discussion_read", "sa_discussion_status",
            },
            with.Except(without, StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryErrorCodeIsDescribedInTheIntegrationSpecificationAndTheCommandReference()
    {
        string root = TestRepositoryRoot.Find();
        string specification = File.ReadAllText(Path.Combine(root, "docs", "SA_AI_INTEGRATION.md"));
        string reference = File.ReadAllText(Path.Combine(root, "docs", "MCP_SERVER_COMMAND_REFERENCE.md"));

        foreach (string code in StockAnalyzer.McpServer.Tools.ToolErrorCodes.All)
        {
            Assert.Contains($"- code: {code}\n", specification);
            Assert.Contains($"| `{code}` |", reference);
        }
    }

    [Fact]
    public void TheCheckSeesAnInventedSectionAndAMissingOne()
    {
        // Self-check: a heading that names no tool and a missing heading must both show up as differences.
        const string text = "### 6.1 `sa_list_tickers`\n\n### 6.2 `sa_not_a_tool`\n\nsome text\n";
        IReadOnlyList<string> documented = DocumentedToolNames(text);

        Assert.Equal(new[] { "sa_list_tickers", "sa_not_a_tool" }, documented);
        Assert.Equal(new[] { "sa_get_candles" }, new[] { "sa_list_tickers", "sa_get_candles" }.Except(documented).ToArray());
        Assert.Equal(new[] { "sa_not_a_tool" }, documented.Except(new[] { "sa_list_tickers", "sa_get_candles" }).ToArray());
    }
}
