using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using StockAnalyzer.TestSupport;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class GetModelAttentionToolTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "sa_attention_tests_" + Guid.NewGuid().ToString("N"));
    private readonly string _modelDir;

    public GetModelAttentionToolTests()
    {
        _modelDir = Path.Combine(_tempDir, "models");
        Directory.CreateDirectory(_modelDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task WithoutEnableExecution_ToolIsNotListedAndCannotBeCalled()
    {
        using var runtime = McpServerComposition.Create(
            McpTestSupport.EmptyDataConfiguration(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())),
            new LogSink(TextWriter.Null),
            enableExecution: false);
        await runtime.Dispatcher.DispatchAsync(McpTestSupport.InitializeMessage, CancellationToken.None);

        string listJson = await runtime.Dispatcher.DispatchAsync(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", CancellationToken.None);
        using JsonDocument list = JsonDocument.Parse(listJson);
        bool found = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Any(t => t.GetProperty("name").GetString() == "sa_get_model_attention");
        Assert.False(found);

        string callJson = await runtime.Dispatcher.DispatchAsync(
            McpTestSupport.CallMessage(2, "sa_get_model_attention", "{\"modelId\":\"active\",\"symbol\":\"7203-T\"}"),
            CancellationToken.None);
        using JsonDocument call = JsonDocument.Parse(callJson);
        Assert.True(call.RootElement.TryGetProperty("error", out var errorElement));
        Assert.Contains("Unknown tool", errorElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task MissingOrActiveNotFound_ThrowsArtifactNotFound()
    {
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles();
        var symbols = new SymbolResolver(gateway);
        var tool = new GetModelAttentionTool(gateway, symbols, registry: registry);

        using var activeDoc = JsonDocument.Parse("{\"modelId\":\"active\",\"symbol\":\"7203-T\"}");
        var ex1 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(activeDoc.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.ArtifactNotFound, ex1.Code);

        using var noSuchDoc = JsonDocument.Parse("{\"modelId\":\"no_such_model\",\"symbol\":\"7203-T\"}");
        var ex2 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(noSuchDoc.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.ArtifactNotFound, ex2.Code);
    }

    [Theory]
    [InlineData("../parent")]
    [InlineData("..\\parent")]
    [InlineData("C:\\Windows")]
    public async Task TraversalModelId_ThrowsInvalidArgument(string traversalId)
    {
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles();
        var symbols = new SymbolResolver(gateway);
        var tool = new GetModelAttentionTool(gateway, symbols, registry: registry);

        using var doc = JsonDocument.Parse($"{{\"modelId\":{JsonSerializer.Serialize(traversalId)},\"symbol\":\"7203-T\"}}");
        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(doc.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task FailedAttention_HistoryShorterThanWindow_ThrowsInsufficientMarketData()
    {
        string modelId = "test_attn_short";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), "{\"schemaVersion\":1,\"modelId\":\"test_attn_short\",\"modelFile\":\"m.onnx\",\"modelHash\":\"a\",\"requiredSidecars\":[],\"contractHash\":\"b\",\"evaluationRevision\":\"c\",\"targetType\":\"classification\",\"timeframe\":\"daily\",\"horizon\":5,\"classOrder\":\"Up,Down\",\"score\":0.5}");

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles(candleCount: 10);
        var symbols = new SymbolResolver(gateway);
        var mockAttention = new MockAttentionService(AttentionResult.Failed("the history is shorter than the model window"));
        var tool = new GetModelAttentionTool(gateway, symbols, registry: registry, attentionService: mockAttention);

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\",\"symbol\":\"7203-T\"}}");
        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(doc.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InsufficientMarketData, ex.Code);
    }

    [Fact]
    public async Task FailedAttention_OtherError_ThrowsInternalErrorSanitized()
    {
        string modelId = "test_attn_fail";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), "{\"schemaVersion\":1,\"modelId\":\"test_attn_fail\",\"modelFile\":\"m.onnx\",\"modelHash\":\"a\",\"requiredSidecars\":[],\"contractHash\":\"b\",\"evaluationRevision\":\"c\",\"targetType\":\"classification\",\"timeframe\":\"daily\",\"horizon\":5,\"classOrder\":\"Up,Down\",\"score\":0.5}");

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles(candleCount: 10);
        var symbols = new SymbolResolver(gateway);
        var mockAttention = new MockAttentionService(AttentionResult.Failed($"Failed to read C:\\secret\\model.onnx internal error"));
        var tool = new GetModelAttentionTool(gateway, symbols, registry: registry, attentionService: mockAttention);

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\",\"symbol\":\"7203-T\"}}");
        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(doc.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InternalError, ex.Code);
        Assert.DoesNotContain("C:\\secret", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InsufficientCandles_ThrowsInsufficientMarketData()
    {
        string modelId = "test_attn_few_bars";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            modelId,
            modelFile = "m.onnx",
            modelHash = "a",
            requiredSidecars = Array.Empty<object>(),
            contractHash = "b",
            evaluationRevision = "c",
            targetType = "classification",
            timeframe = "daily",
            horizon = 5,
            classOrder = "Up,Down",
            score = 0.5
        }));

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles(candleCount: 1); // Less than MinWindow (2)
        var symbols = new SymbolResolver(gateway);
        var tool = new GetModelAttentionTool(gateway, symbols, registry: registry);

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\",\"symbol\":\"7203-T\"}}");
        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(doc.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InsufficientMarketData, ex.Code);
    }

    [Fact]
    public async Task ObligationA_MatchesCoreAttentionResult()
    {
        string modelId = "test_attn_001";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            modelId,
            modelFile = "m.onnx",
            modelHash = "a",
            requiredSidecars = Array.Empty<object>(),
            contractHash = "b",
            evaluationRevision = "c",
            targetType = "classification",
            timeframe = "daily",
            horizon = 5,
            classOrder = "Up,Down",
            score = 0.5
        }));

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles(candleCount: 10);
        var symbols = new SymbolResolver(gateway);

        var key = new AttentionRequestKey(modelId, "7203-T", "daily", DateTime.UtcNow, 0);
        var timestamps = ImmutableArray.Create(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        var weights = ImmutableArray.Create(0.2f, 0.3f, 0.5f);
        var relative = ImmutableArray.Create(0.4f, 0.6f, 1.0f);
        var snapshot = new AttentionSnapshot(key, Heads: 4, timestamps, weights, relative);
        var mockAttention = new MockAttentionService(AttentionResult.Ready(snapshot));

        var tool = new GetModelAttentionTool(gateway, symbols, registry: registry, attentionService: mockAttention);

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\",\"symbol\":\"7203-T\"}}");
        string json = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);
        using var res = JsonDocument.Parse(json);
        var root = res.RootElement;

        Assert.Equal(modelId, root.GetProperty("modelId").GetString());
        Assert.Equal("7203-T", root.GetProperty("symbol").GetString());
        Assert.Equal("ready", root.GetProperty("status").GetString());

        var contract = root.GetProperty("modelContract");
        Assert.Equal(3, contract.GetProperty("windowSize").GetInt32());
        Assert.Equal(4, contract.GetProperty("heads").GetInt32());
        Assert.Equal("attn_lstm", contract.GetProperty("architecture").GetString());

        var attention = root.GetProperty("attention").EnumerateArray().ToList();
        Assert.Equal(3, attention.Count);
        Assert.Equal(-2, attention[0].GetProperty("barOffset").GetInt32());
        Assert.Equal(-1, attention[1].GetProperty("barOffset").GetInt32());
        Assert.Equal(0, attention[2].GetProperty("barOffset").GetInt32());
        Assert.Equal(0.5f, attention[2].GetProperty("weight").GetSingle());
        Assert.Equal(1.0f, attention[2].GetProperty("relative").GetSingle());
    }

    [Fact]
    public async Task ObligationB_ZeroMutation_LeavesFilesUnchanged()
    {
        string modelId = "test_attn_mut";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), "{\"schemaVersion\":1,\"modelId\":\"test_attn_mut\",\"modelFile\":\"m.onnx\",\"modelHash\":\"a\",\"requiredSidecars\":[],\"contractHash\":\"b\",\"evaluationRevision\":\"c\",\"targetType\":\"classification\",\"timeframe\":\"daily\",\"horizon\":5,\"classOrder\":\"Up,Down\",\"score\":0.5}");

        var filesBefore = Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories)
            .Select(f => (Path: f, Hash: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), Time: File.GetLastWriteTimeUtc(f)))
            .OrderBy(f => f.Path)
            .ToList();

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles(candleCount: 10);
        var symbols = new SymbolResolver(gateway);
        var mockAttention = new MockAttentionService(AttentionResult.Unsupported("no_sidecar"));
        var tool = new GetModelAttentionTool(gateway, symbols, registry: registry, attentionService: mockAttention);

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\",\"symbol\":\"7203-T\"}}");
        _ = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);

        var filesAfter = Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories)
            .Select(f => (Path: f, Hash: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), Time: File.GetLastWriteTimeUtc(f)))
            .OrderBy(f => f.Path)
            .ToList();

        Assert.Equal(filesBefore.Count, filesAfter.Count);
        for (int i = 0; i < filesBefore.Count; i++)
        {
            Assert.Equal(filesBefore[i].Path, filesAfter[i].Path);
            Assert.Equal(filesBefore[i].Hash, filesAfter[i].Hash);
            Assert.Equal(filesBefore[i].Time, filesAfter[i].Time);
        }
    }

    [Fact]
    public async Task ObligationC_ContainsNoAbsoluteFilePaths()
    {
        string modelId = "test_attn_path";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), "{\"schemaVersion\":1,\"modelId\":\"test_attn_path\",\"modelFile\":\"m.onnx\",\"modelHash\":\"a\",\"requiredSidecars\":[],\"contractHash\":\"b\",\"evaluationRevision\":\"c\",\"targetType\":\"classification\",\"timeframe\":\"daily\",\"horizon\":5,\"classOrder\":\"Up,Down\",\"score\":0.5}");

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles(candleCount: 10);
        var symbols = new SymbolResolver(gateway);
        var mockAttention = new MockAttentionService(AttentionResult.Unsupported("no_sidecar"));
        var tool = new GetModelAttentionTool(gateway, symbols, registry: registry, attentionService: mockAttention);

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\",\"symbol\":\"7203-T\"}}");
        string json = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);

        Assert.DoesNotContain(":\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tempDir, json, StringComparison.OrdinalIgnoreCase);
    }

    private void WriteManifest(string modelId, string timeframe)
    {
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            modelId,
            modelFile = "m.onnx",
            modelHash = "a",
            requiredSidecars = Array.Empty<object>(),
            contractHash = "b",
            evaluationRevision = "c",
            targetType = "classification",
            timeframe,
            horizon = 5,
            classOrder = "Up,Down",
            score = 0.5
        }));
    }

    private static AttentionResult ReadyResult(string modelId, string timeframe)
    {
        var key = new AttentionRequestKey(modelId, "7203-T", timeframe, new DateTime(2026, 1, 9), 0);
        var snapshot = new AttentionSnapshot(
            key,
            Heads: 2,
            ImmutableArray.Create(new DateTime(2026, 1, 2), new DateTime(2026, 1, 9)),
            ImmutableArray.Create(0.4f, 0.6f),
            ImmutableArray.Create(0.6666667f, 1.0f));
        return AttentionResult.Ready(snapshot);
    }

    [Theory]
    [InlineData("daily", TimeFrame.D1, "Daily")]
    [InlineData("weekly", TimeFrame.W1, "Weekly")]
    [InlineData("monthly", TimeFrame.MN1, "Monthly")]
    [InlineData("Weekly", TimeFrame.W1, "Weekly")]
    public async Task TheBarsAreReadInTheModelsOwnTimeFrame_AndTheKeyNamesTheFetchedFrame(string manifestTimeframe, TimeFrame expectedFrame, string expectedKeyFrame)
    {
        string modelId = "test_attn_frame_" + manifestTimeframe.ToLowerInvariant();
        WriteManifest(modelId, manifestTimeframe);
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles(candleCount: 10);
        var attention = new MockAttentionService(ReadyResult(modelId, manifestTimeframe));
        var tool = new GetModelAttentionTool(gateway, new SymbolResolver(gateway), registry: registry, attentionService: attention);

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\",\"symbol\":\"7203-T\"}}");
        _ = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);

        Assert.Equal(new[] { (expectedFrame, false) }, gateway.Requests);
        Assert.NotNull(attention.LastKey);
        Assert.Equal(expectedKeyFrame, attention.LastKey!.Timeframe);
    }

    [Fact]
    public async Task AnAsOfDateUsesTheAsOfReadInTheModelsOwnTimeFrame()
    {
        string modelId = "test_attn_frame_asof";
        WriteManifest(modelId, "weekly");
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles(candleCount: 10);
        var tool = new GetModelAttentionTool(gateway, new SymbolResolver(gateway), registry: registry, attentionService: new MockAttentionService(ReadyResult(modelId, "weekly")));

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\",\"symbol\":\"7203-T\",\"asOfDate\":\"2026-01-09\"}}");
        _ = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);

        Assert.Equal(new[] { (TimeFrame.W1, true) }, gateway.Requests);
    }

    [Fact]
    public async Task AMonthlyModelWithTooFewMonthlyBarsIsInsufficientMarketData_EvenWhenDailyBarsWouldSuffice()
    {
        string modelId = "test_attn_few_monthly";
        WriteManifest(modelId, "monthly");
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles(candleCount: 1); // below AttentionContract.MinWindow
        var tool = new GetModelAttentionTool(gateway, new SymbolResolver(gateway), registry: registry, attentionService: new MockAttentionService(ReadyResult(modelId, "monthly")));

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\",\"symbol\":\"7203-T\"}}");
        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(doc.RootElement, CancellationToken.None));

        Assert.Equal(ToolErrorCodes.InsufficientMarketData, ex.Code);
        Assert.Equal(new[] { (TimeFrame.MN1, false) }, gateway.Requests);
    }

    [Theory]
    [InlineData("H1")]
    [InlineData("hourly")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("6")]
    public async Task AModelTimeFrameTheServerCannotRead_IsUnsupportedTimeFrame_BeforeAnyBarIsRead(string manifestTimeframe)
    {
        string modelId = "test_attn_bad_frame";
        WriteManifest(modelId, manifestTimeframe);
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new MockGatewayWithCandles(candleCount: 10);
        var tool = new GetModelAttentionTool(gateway, new SymbolResolver(gateway), registry: registry, attentionService: new MockAttentionService(ReadyResult(modelId, "daily")));

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\",\"symbol\":\"7203-T\"}}");
        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(doc.RootElement, CancellationToken.None));

        Assert.Equal(ToolErrorCodes.UnsupportedTimeFrame, ex.Code);
        Assert.Equal("modelId", ex.Parameter);
        Assert.Empty(gateway.Requests);
    }

    private sealed class MockGatewayWithCandles : EmptyReadOnlyStockAnalyzerGateway
    {
        private readonly int _candleCount;

        public MockGatewayWithCandles(int candleCount = 50)
        {
            _candleCount = candleCount;
        }

        public override Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "7203-T", "MSFT" });

        /// <summary>Every time frame the tool asked for, in order, and whether the as-of overload was used.</summary>
        public List<(TimeFrame Frame, bool AsOf)> Requests { get; } = new();

        public override Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
        {
            Requests.Add((timeFrame, false));
            return Task.FromResult<IReadOnlyList<CandleData>>(GenerateCandles(Math.Min(count, _candleCount)));
        }

        public override Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default)
        {
            Requests.Add((timeFrame, true));
            return Task.FromResult<IReadOnlyList<CandleData>>(GenerateCandles(Math.Min(count, _candleCount)));
        }

        private static List<CandleData> GenerateCandles(int n)
        {
            var list = new List<CandleData>();
            var start = new DateTime(2026, 1, 1);
            for (int i = 0; i < n; i++)
            {
                list.Add(new CandleData(start.AddDays(i), 100, 105, 95, 102, 1000));
            }
            return list;
        }
    }

    private sealed class MockAttentionService : IAttentionService
    {
        private readonly AttentionResult _result;

        public MockAttentionService(AttentionResult result)
        {
            _result = result;
        }

        public AttentionRequestKey? LastKey { get; private set; }

        public Task<AttentionResult> ComputeAsync(AttentionRequestKey key, IReadOnlyList<CandleData> history, CancellationToken cancellationToken = default)
        {
            LastKey = key;
            return Task.FromResult(_result);
        }

        public Task<AttentionResult> ComputeMemberAsync(AttentionRequestKey key, IReadOnlyList<CandleData> history, CancellationToken cancellationToken = default)
        {
            LastKey = key;
            return Task.FromResult(_result);
        }
    }
}
