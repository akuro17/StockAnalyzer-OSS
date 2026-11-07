using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.TestSupport;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// The tools/list the server reported BEFORE the discussion board was added (recorded as the first step of task T3, before any shared code changed), kept as a
/// byte-level baseline: the SHA-256 of every tool entry exactly as it is written to the client, for the default tool set and for <c>--enable-execution</c>.
/// Adding the discussion defaults to <c>IMcpTool</c>, the executor and the dispatcher must not change a single byte of either list; a later tool added on purpose
/// extends the baseline file in the same change.
/// </summary>
public sealed class ToolsListBaselineTests
{
    private const string BaselineRelativePath = "Tests/McpServer/Golden/tools_list_before_discussion.txt";
    private const string DefaultHeader = "[default]";
    private const string ExecutionHeader = "[execution]";
    private const string ResponseKey = "response";

    private readonly string _emptyRoot = Path.Combine(Path.GetTempPath(), "dhub-baseline-" + Guid.NewGuid().ToString("N"));

    private static Dictionary<string, Dictionary<string, string>> ReadBaseline()
    {
        string path = Path.Combine(TestRepositoryRoot.Find(), BaselineRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? current = null;
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
                sections[line] = current;
                continue;
            }

            string[] parts = line.Split('\t');
            Assert.Equal(2, parts.Length);
            current!.Add(parts[0], parts[1]);
        }

        return sections;
    }

    private async Task<(Dictionary<string, string> Entries, string ResponseHash)> CurrentAsync(bool enableExecution)
    {
        using McpServerRuntime runtime = McpServerComposition.Create(McpTestSupport.EmptyDataConfiguration(_emptyRoot), new LogSink(new StringWriter()), enableExecution: enableExecution);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        string response = (await runtime.Dispatcher.DispatchAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", CancellationToken.None))!;
        using JsonDocument document = JsonDocument.Parse(response);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement tool in document.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            entries.Add(tool.GetProperty("name").GetString()!, Sha256(tool.GetRawText()));
        }

        return (entries, Sha256(response));
    }

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    [Theory]
    [InlineData(false, DefaultHeader)]
    [InlineData(true, ExecutionHeader)]
    public async Task EveryToolEntryOfTheToolListIsByteIdenticalToTheBaselineBeforeTheDiscussionBoard(bool enableExecution, string header)
    {
        Dictionary<string, string> baseline = ReadBaseline()[header];
        (Dictionary<string, string> entries, string responseHash) = await CurrentAsync(enableExecution);

        Assert.Equal(baseline.Keys.Where(name => name != ResponseKey).OrderBy(name => name, StringComparer.Ordinal), entries.Keys.OrderBy(name => name, StringComparer.Ordinal));
        foreach ((string name, string hash) in entries)
        {
            Assert.True(baseline[name] == hash, $"The tools/list entry of '{name}' changed.");
        }

        Assert.Equal(baseline[ResponseKey], responseHash);
    }

    [Fact]
    public void TheBaselineHoldsBothToolSetsAndTheExecutionSetIsTheLargerOne()
    {
        Dictionary<string, Dictionary<string, string>> baseline = ReadBaseline();

        Assert.Contains(DefaultHeader, baseline.Keys);
        Assert.Contains(ExecutionHeader, baseline.Keys);
        Assert.True(baseline[ExecutionHeader].Count > baseline[DefaultHeader].Count);
        Assert.All(baseline.Values, section => Assert.Contains(ResponseKey, section.Keys));
    }
}
