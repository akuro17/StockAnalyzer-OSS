using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Execution;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
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

    private sealed class StubRlRegistry(string modelId, string symbol) : IRlModelGenerationRegistry
    {
        public string? RlActiveId => null;
        public IReadOnlyList<RlGeneration> ListRl() => Array.Empty<RlGeneration>();
        public ModelGenerationLease? AcquireRl(string id) => null;
        public ModelGenerationLease? AcquireRlActive() => null;
        public RlGeneration? GetRlGeneration(string id)
            => id == modelId ? new RlGeneration(id, symbol, "rev", RlContract.ContractHash, "eval", 0m, 0m, new Dictionary<string, string>(), DateTimeOffset.UtcNow) : null;
        public Task<bool> ActivateRlAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<RlGeneration> RegisterRlAsync(string artifactDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubRlDialogDataService(
        IRlModelGenerationRegistry registry,
        Func<CancellationToken, Task<RlAnchorRange>> getRange)
        : RlDialogDataService(registry, new QValueService(registry))
    {
        public override Task<RlAnchorRange> GetAnchorRangeAsync(string modelId, string symbol, CancellationToken ct)
            => getRange(ct);
    }

    [Fact]
    public async Task CancellationAndTimeout_PropagateOutOfPreflightInsteadOfReturningCanReplayFalse()
    {
        const string modelId = "m1";
        var registry = new StubRlRegistry(modelId, "7203-T");
        var symbols = new SymbolResolver(new EmptyReadOnlyStockAnalyzerGateway());
        using var started = new SemaphoreSlim(0);
        var dataService = new StubRlDialogDataService(registry, async ct =>
        {
            started.Release();
            await Task.Delay(Timeout.Infinite, ct);
            return default;
        });

        var tool = new CheckRlPlaybackPreflightTool(symbols, registry, dataService, sanitizer: new ErrorMessageSanitizer(Array.Empty<string?>()));

        // 1. Timeout -> EXECUTION_TIMEOUT (isError: true)
        McpMessageDispatcher timeoutDispatcher = await McpTestSupport.InitializedAsync(
            new IMcpTool[] { tool },
            new McpLimits { ToolTimeoutSeconds = 1, HeavyToolTimeoutSeconds = 1 });
        using JsonDocument timeoutResp = (await McpTestSupport.CallAsync(timeoutDispatcher, "sa_check_rl_playback_preflight", """{"modelId":"m1"}"""))!;
        Assert.True(McpTestSupport.IsError(timeoutResp));
        Assert.Equal(ToolErrorCodes.ExecutionTimeout, McpTestSupport.ErrorCodeOf(timeoutResp));

        // 2. Client notifications/cancelled -> null (no response)
        McpMessageDispatcher cancelDispatcher = await McpTestSupport.InitializedAsync(
            new IMcpTool[] { tool },
            new McpLimits { ToolTimeoutSeconds = 30, HeavyToolTimeoutSeconds = 30 });
        Task<JsonDocument?> callTask = McpTestSupport.CallAsync(cancelDispatcher, "sa_check_rl_playback_preflight", """{"modelId":"m1"}""", id: 91);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10)));
        await cancelDispatcher.DispatchAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":91}}""", CancellationToken.None);
        Assert.Null(await callTask);
    }

    [Theory]
    [InlineData(true, @"Cannot read C:\Users\someone\secret\Data\7203-T.parquet", @"Cannot read <path>\Data\7203-T.parquet")]
    [InlineData(true, @"Cannot read D:\Other\Data\7203-T.parquet", "Cannot read <path>")]
    [InlineData(false, "Failed at /home/secret/model.onnx", "Failed at <path>")]
    [InlineData(false, "   ", "RL playback preflight check failed.")]
    public async Task ReplayAndUnexpectedExceptions_SanitizePathsAndFallbackWhenBlank(bool isReplayException, string rawMessage, string expectedReason)
    {
        const string modelId = "m1";
        var registry = new StubRlRegistry(modelId, "7203-T");
        var symbols = new SymbolResolver(new EmptyReadOnlyStockAnalyzerGateway());
        var dataService = new StubRlDialogDataService(registry, _ =>
            isReplayException
                ? Task.FromException<RlAnchorRange>(new RlExternalReplayException(RlExternalReplayRejection.SourceUnavailable, rawMessage))
                : Task.FromException<RlAnchorRange>(new InvalidOperationException(rawMessage)));

        var tool = new CheckRlPlaybackPreflightTool(symbols, registry, dataService, sanitizer: new ErrorMessageSanitizer(new[] { @"C:\Users\someone\secret" }));
        McpMessageDispatcher dispatcher = await McpTestSupport.InitializedAsync(new IMcpTool[] { tool });

        using JsonDocument resp = (await McpTestSupport.CallAsync(dispatcher, "sa_check_rl_playback_preflight", """{"modelId":"m1"}"""))!;
        Assert.False(McpTestSupport.IsError(resp));
        JsonElement result = McpTestSupport.Structured(resp);
        Assert.False(result.GetProperty("canReplay").GetBoolean());
        string actualReason = result.GetProperty("reason").GetString()!;
        Assert.Equal(expectedReason, actualReason);
        Assert.DoesNotContain("secret", actualReason, StringComparison.OrdinalIgnoreCase);
    }
}
