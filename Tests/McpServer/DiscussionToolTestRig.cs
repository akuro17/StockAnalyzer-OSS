using System.IO;
using System.Text.Json;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;

namespace StockAnalyzer.Tests.McpServer;

/// <summary>
/// Shared set-up of the discussion tool tests: a real composition with an empty data folder, JSON-RPC calls with running request ids and the create/join steps.
/// Every runtime and the folder are released with the rig.
/// </summary>
internal sealed class DiscussionToolTestRig : IDisposable
{
    private readonly string _emptyRoot;
    private readonly List<McpServerRuntime> _runtimes = new();
    private int _requestId = 100;

    public DiscussionToolTestRig(string folderPrefix)
    {
        _emptyRoot = Path.Combine(Path.GetTempPath(), folderPrefix + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        foreach (McpServerRuntime runtime in _runtimes)
        {
            runtime.Dispose();
        }

        if (Directory.Exists(_emptyRoot))
        {
            Directory.Delete(_emptyRoot, recursive: true);
        }
    }

    public async Task<McpServerRuntime> StartAsync(bool compactDefaults = false)
    {
        McpServerRuntime runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(_emptyRoot),new LogSink(new StringWriter()), enableDiscussion: true, compactDefaults: compactDefaults);
        _runtimes.Add(runtime);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);
        return runtime;
    }

    public async Task<JsonDocument> CallAsync(McpServerRuntime runtime, string tool, string argumentsJson)
        => (await McpTestSupport.CallAsync(runtime.Dispatcher, tool, argumentsJson, Interlocked.Increment(ref _requestId)))!;

    /// <summary>Creates a discussion; <paramref name="argumentsJson"/> holds the create arguments.</summary>
    public async Task<string> CreateAsync(McpServerRuntime runtime, string argumentsJson)
    {
        using JsonDocument created = await CallAsync(runtime, "sa_discussion_create", argumentsJson);
        return McpTestSupport.Structured(created).GetProperty("sessionId").GetString()!;
    }

    public async Task<string> JoinAsync(McpServerRuntime runtime, string sessionId, string name)
    {
        using JsonDocument joined = await CallAsync(runtime, "sa_discussion_join", $$"""{"sessionId":"{{sessionId}}","displayName":"{{name}}"}""");
        return McpTestSupport.Structured(joined).GetProperty("participantToken").GetString()!;
    }
}
