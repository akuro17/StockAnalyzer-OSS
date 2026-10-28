using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Protocol;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public class CheckRlPlaybackPreflightToolZeroMutationTests
{
    [Fact]
    public async Task PreflightInReadOnlyMode_DoesNotCreateCoordinationLockOrMutateDisk()
    {
        string generationsDir = PathDiscovery.ResolveModelGenerationsDirectoryWithoutCreating();
        string lockFile = Path.Combine(generationsDir, ".coordination", "registry.lock");

        if (File.Exists(lockFile))
        {
            try { File.Delete(lockFile); } catch { }
        }

        using var runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())),
            new LogSink(TextWriter.Null),
            enableExecution: false);

        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, System.Threading.CancellationToken.None);

        string responseJson = await runtime.Dispatcher.DispatchAsync(
            McpTestSupport.CallMessage(1, "sa_check_rl_playback_preflight", """{"modelId":"test_model"}"""),
            System.Threading.CancellationToken.None);
        using JsonDocument response = JsonDocument.Parse(responseJson);

        Assert.False(McpTestSupport.IsError(response));
        JsonElement result = McpTestSupport.Structured(response);
        Assert.False(result.GetProperty("canReplay").GetBoolean());
        Assert.Contains("test_model", result.GetProperty("reason").GetString());

        // Zero-mutation invariant: no coordination lock was created on disk in read-only mode
        Assert.False(File.Exists(lockFile));
    }

    [Fact]
    public async Task ToolListDescription_NamesTheTransientTemporaryFolderInsteadOfClaimingZeroMutation()
    {
        using var runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())),
            new LogSink(TextWriter.Null),
            enableExecution: false);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, System.Threading.CancellationToken.None);

        string listJson = await runtime.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", System.Threading.CancellationToken.None);
        using JsonDocument list = JsonDocument.Parse(listJson);
        string description = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "sa_check_rl_playback_preflight")
            .GetProperty("description").GetString()!;

        Assert.Contains("temporary folder", description);
        Assert.DoesNotContain("zero-mutation", description, StringComparison.OrdinalIgnoreCase);
    }
}
