using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.McpServer;
using StockAnalyzer.McpServer.Limits;
using StockAnalyzer.McpServer.Protocol;
using StockAnalyzer.McpServer.Tools;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class ListModelsToolTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "sa_list_models_tests_" + Guid.NewGuid().ToString("N"));
    private readonly string _modelDir;
    private readonly string _rlDir;

    public ListModelsToolTests()
    {
        _modelDir = Path.Combine(_tempDir, "models");
        _rlDir = Path.Combine(_modelDir, "rl");
        Directory.CreateDirectory(_modelDir);
        Directory.CreateDirectory(_rlDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { }
    }

    [Fact]
    public async Task EmptyRegistry_ReturnsEmptyModelsAndNullActiveIds()
    {
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);
        var limits = new McpLimits();
        var tool = new ListModelsTool(symbols, registry, limits);

        using var doc = JsonDocument.Parse("{}");
        string json = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);
        using var res = JsonDocument.Parse(json);
        var root = res.RootElement;

        Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(0, root.GetProperty("offset").GetInt32());
        Assert.Equal(limits.MaxListedModels, root.GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("returnedCount").GetInt32());
        Assert.False(root.GetProperty("hasMore").GetBoolean());

        var active = root.GetProperty("activeModelId");
        Assert.Equal(JsonValueKind.Null, active.GetProperty("supervised").ValueKind);
        Assert.Equal(JsonValueKind.Null, active.GetProperty("reinforcementLearning").ValueKind);

        Assert.Empty(root.GetProperty("models").EnumerateArray());
    }

    [Fact]
    public async Task ObligationA_MatchesCoreRegistryEnumerate()
    {
        // Seed one supervised model and one RL model
        string supId = "sup_test_001";
        string supDir = Path.Combine(_modelDir, supId);
        Directory.CreateDirectory(supDir);
        string manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            modelId = supId,
            modelFile = "model.onnx",
            modelHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            requiredSidecars = Array.Empty<object>(),
            contractHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            evaluationRevision = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            targetType = "classification",
            timeframe = "daily",
            horizon = 5,
            classOrder = "Up,Down,Neutral",
            score = 0.72
        });
        File.WriteAllText(Path.Combine(supDir, "manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(supDir, "model.onnx.scaler.json"), "{\"window_size\":20}");

        string rlId = "rl_test_001";
        string rlModelDir = Path.Combine(_rlDir, rlId);
        Directory.CreateDirectory(rlModelDir);
        string rlManifestJson = JsonSerializer.Serialize(new
        {
            ModelId = rlId,
            Symbol = "7203-T",
            SourceRevision = "rev1",
            ContractHash = "hash1",
            EvaluationRevision = "rev_eval",
            ValidationReturn = 15.5m,
            OosReturn = 12.3m,
            Files = new Dictionary<string, string>(),
            CreatedUtc = DateTimeOffset.UtcNow
        });
        File.WriteAllText(Path.Combine(rlModelDir, "manifest.json"), rlManifestJson);

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);
        var limits = new McpLimits();
        var tool = new ListModelsTool(symbols, registry, limits);

        IReadOnlyList<ModelCatalogEntry> coreEntries = registry.EnumerateCatalogEntries();
        Assert.Equal(2, coreEntries.Count);

        using var doc = JsonDocument.Parse("{}");
        string json = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);
        using var res = JsonDocument.Parse(json);
        var root = res.RootElement;

        Assert.Equal(coreEntries.Count, root.GetProperty("totalCount").GetInt32());
        var returnedModels = root.GetProperty("models").EnumerateArray().ToList();
        Assert.Equal(2, returnedModels.Count);

        for (int i = 0; i < coreEntries.Count; i++)
        {
            var expected = coreEntries[i];
            var actual = returnedModels[i];

            Assert.Equal(expected.ModelId, actual.GetProperty("modelId").GetString());
            Assert.Equal(expected.Kind, actual.GetProperty("kind").GetString());
            Assert.Equal(expected.Timeframe, actual.GetProperty("timeFrame").GetString());
            Assert.Equal(expected.WindowSize, actual.GetProperty("contract").GetProperty("windowSize").GetInt32());
            Assert.Equal(expected.HorizonBars, actual.GetProperty("contract").GetProperty("horizonBars").GetInt32());
        }
    }

    [Fact]
    public async Task ObligationB_ZeroMutation_LeavesFilesAndTreeUnchanged()
    {
        // Seed a model
        string supId = "sup_zero_mut";
        string supDir = Path.Combine(_modelDir, supId);
        Directory.CreateDirectory(supDir);
        string manifestPath = Path.Combine(supDir, "manifest.json");
        File.WriteAllText(manifestPath, "{\"schemaVersion\":1,\"modelId\":\"sup_zero_mut\",\"modelFile\":\"m.onnx\",\"modelHash\":\"a\",\"requiredSidecars\":[],\"contractHash\":\"b\",\"evaluationRevision\":\"c\",\"targetType\":\"regression\",\"timeframe\":\"daily\",\"horizon\":1,\"classOrder\":\"\",\"score\":0.5}");

        var filesBefore = Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories)
            .Select(f => (Path: f, Hash: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), Time: File.GetLastWriteTimeUtc(f)))
            .OrderBy(f => f.Path)
            .ToList();

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);
        var limits = new McpLimits();
        var tool = new ListModelsTool(symbols, registry, limits);

        using var doc = JsonDocument.Parse("{}");
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
    public async Task ObligationC_OutputContainsNoAbsoluteFilePaths()
    {
        string supId = "sup_path_check";
        string supDir = Path.Combine(_modelDir, supId);
        Directory.CreateDirectory(supDir);
        File.WriteAllText(Path.Combine(supDir, "manifest.json"), "{\"schemaVersion\":1,\"modelId\":\"sup_path_check\",\"modelFile\":\"m.onnx\",\"modelHash\":\"a\",\"requiredSidecars\":[],\"contractHash\":\"b\",\"evaluationRevision\":\"c\",\"targetType\":\"regression\",\"timeframe\":\"daily\",\"horizon\":1,\"classOrder\":\"\",\"score\":0.5}");

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);
        var limits = new McpLimits();
        var tool = new ListModelsTool(symbols, registry, limits);

        using var doc = JsonDocument.Parse("{}");
        string json = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);

        Assert.DoesNotContain(":\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tempDir, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_modelDir, json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidArguments_ThrowsToolException()
    {
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);
        var limits = new McpLimits();
        var tool = new ListModelsTool(symbols, registry, limits);

        using var invalidKind = JsonDocument.Parse("{\"kind\":\"InvalidKind\"}");
        var ex1 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(invalidKind.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex1.Code);

        using var negativeOffset = JsonDocument.Parse("{\"offset\":-1}");
        var ex2 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(negativeOffset.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex2.Code);
    }
}
