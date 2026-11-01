using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.ReadOnly;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.McpServer.Tools;
using Xunit;

namespace StockAnalyzer.Tests.McpServer;

public sealed class GetModelExplanationToolTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "sa_explanation_tests_" + Guid.NewGuid().ToString("N"));
    private readonly string _modelDir;

    public GetModelExplanationToolTests()
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
    public async Task MissingOrActiveNotFound_ThrowsArtifactNotFound()
    {
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);
        var tool = new GetModelExplanationTool(gateway, symbols, registry);

        using var activeDoc = JsonDocument.Parse("{\"modelId\":\"active\"}");
        var ex1 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(activeDoc.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.ArtifactNotFound, ex1.Code);

        using var noSuchDoc = JsonDocument.Parse("{\"modelId\":\"non_existent_id\"}");
        var ex2 = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(noSuchDoc.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.ArtifactNotFound, ex2.Code);
    }

    [Theory]
    [InlineData("../parent")]
    [InlineData("..\\parent")]
    [InlineData("C:\\Windows")]
    [InlineData("has/slash")]
    public async Task TraversalModelId_ThrowsInvalidArgument(string traversalId)
    {
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);
        var tool = new GetModelExplanationTool(gateway, symbols, registry);

        using var doc = JsonDocument.Parse($"{{\"modelId\":{JsonSerializer.Serialize(traversalId)}}}");
        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(doc.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task AsOfDateWithoutSymbol_ThrowsInvalidArgument()
    {
        string modelId = "test_gen_asof";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), "{\"schemaVersion\":1,\"modelId\":\"test_gen_asof\",\"modelFile\":\"m.onnx\",\"modelHash\":\"a\",\"requiredSidecars\":[],\"contractHash\":\"b\",\"evaluationRevision\":\"c\",\"targetType\":\"classification\",\"timeframe\":\"daily\",\"horizon\":5,\"classOrder\":\"Up,Down\",\"score\":0.5}");

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);
        var tool = new GetModelExplanationTool(gateway, symbols, registry);

        using var doc = JsonDocument.Parse("{\"modelId\":\"test_gen_asof\",\"asOfDate\":\"2024-01-01\"}");
        var ex = await Assert.ThrowsAsync<ToolException>(() => tool.InvokeAsync(doc.RootElement, CancellationToken.None));
        Assert.Equal(ToolErrorCodes.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task ObligationA_MatchesCoreServiceDirectCall()
    {
        string modelId = "test_gen_01";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            modelId,
            modelFile = "model.onnx",
            modelHash = "a",
            requiredSidecars = Array.Empty<object>(),
            contractHash = "b",
            evaluationRevision = "c",
            targetType = "classification",
            timeframe = "daily",
            horizon = 5,
            classOrder = "Up,Down,Neutral",
            score = 0.8
        }));
        File.WriteAllText(Path.Combine(_modelDir, "active.json"), $"{{\"activeId\":\"{modelId}\"}}");

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);

        var mockExplainability = new MockExplainabilityService(
            new ModelExplanationLoad(ExplanationAvailability.Available, CreateDummyExplanation()),
            new ModelCalibrationLoad(CalibrationAvailability.Active, CreateDummyCalibration()));

        var tool = new GetModelExplanationTool(gateway, symbols, registry, explainabilityService: mockExplainability);

        using var doc = JsonDocument.Parse("{\"modelId\":\"active\"}");
        string json = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);
        using var res = JsonDocument.Parse(json);
        var root = res.RootElement;

        Assert.Equal("active", root.GetProperty("modelId").GetString());
        Assert.Equal(modelId, root.GetProperty("resolvedModelId").GetString());

        var exp = root.GetProperty("explanation");
        Assert.Equal("Available", exp.GetProperty("availability").GetString());
        Assert.Equal(20, exp.GetProperty("windowSize").GetInt32());
        Assert.Equal("daily", exp.GetProperty("timeframe").GetString());

        var cal = root.GetProperty("calibration");
        Assert.Equal("Active", cal.GetProperty("availability").GetString());
        Assert.Equal(1.5, cal.GetProperty("temperature").GetDouble());

        // B-13a compliance: topContributingFeatures must NOT be output
        Assert.False(root.TryGetProperty("topContributingFeatures", out _));
        Assert.False(exp.TryGetProperty("topContributingFeatures", out _));
    }

    [Fact]
    public async Task ObligationB_ZeroMutation_LeavesFilesUnchanged()
    {
        string modelId = "test_gen_mut";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), "{\"schemaVersion\":1,\"modelId\":\"test_gen_mut\",\"modelFile\":\"m.onnx\",\"modelHash\":\"a\",\"requiredSidecars\":[],\"contractHash\":\"b\",\"evaluationRevision\":\"c\",\"targetType\":\"classification\",\"timeframe\":\"daily\",\"horizon\":5,\"classOrder\":\"Up,Down\",\"score\":0.5}");

        var filesBefore = Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories)
            .Select(f => (Path: f, Hash: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), Time: File.GetLastWriteTimeUtc(f)))
            .OrderBy(f => f.Path)
            .ToList();

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);
        var tool = new GetModelExplanationTool(gateway, symbols, registry);

        using var doc = JsonDocument.Parse("{\"modelId\":\"test_gen_mut\"}");
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
        string modelId = "test_gen_path";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), "{\"schemaVersion\":1,\"modelId\":\"test_gen_path\",\"modelFile\":\"m.onnx\",\"modelHash\":\"a\",\"requiredSidecars\":[],\"contractHash\":\"b\",\"evaluationRevision\":\"c\",\"targetType\":\"classification\",\"timeframe\":\"daily\",\"horizon\":5,\"classOrder\":\"Up,Down\",\"score\":0.5}");

        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new EmptyReadOnlyStockAnalyzerGateway();
        var symbols = new SymbolResolver(gateway);
        var tool = new GetModelExplanationTool(gateway, symbols, registry);

        using var doc = JsonDocument.Parse("{\"modelId\":\"test_gen_path\"}");
        string json = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);

        Assert.DoesNotContain(":\\", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tempDir, json, StringComparison.OrdinalIgnoreCase);
    }

    private static ModelExplanation CreateDummyExplanation()
    {
        return new ModelExplanation(
            SchemaVersion: 1,
            ModelSha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            DataRevision: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            FeatureContractHash: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            TargetContractHash: "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            Timeframe: "daily",
            WindowSize: 20,
            BaseChannels: ImmutableArray.Create("open", "high", "low", "close", "volume"),
            OrderedChannels: ImmutableArray.Create("open", "high", "low", "close", "volume"),
            Status: "available",
            Classes: ImmutableArray.Create("Up", "Down", "Neutral")
        );
    }

    private static ModelCalibration CreateDummyCalibration()
    {
        return new ModelCalibration(
            SchemaVersion: 1,
            ModelSha256: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            DataRevision: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            FeatureContractHash: "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            ClassOrder: "Up,Down,Neutral",
            Method: "temperature",
            Score: "log_probability",
            Epsilon: 1e-15,
            Temperature: 1.5,
            Status: "available",
            Fit: new CalibrationFit("calibration", 100, ImmutableArray<CalibrationPeriod>.Empty, "rev", 10),
            Evaluation: new CalibrationEvaluation(
                new CalibrationMetrics(50, new CalibrationScores(0.5, 0.2, 0.05), new CalibrationScores(0.4, 0.15, 0.02)),
                new CalibrationMetrics(50, new CalibrationScores(0.6, 0.25, 0.06), new CalibrationScores(0.45, 0.18, 0.03)))
        );
    }

    private sealed class MockExplainabilityService : IModelExplainabilityService
    {
        private readonly ModelExplanationLoad _explanation;
        private readonly ModelCalibrationLoad _calibration;

        public MockExplainabilityService(ModelExplanationLoad explanation, ModelCalibrationLoad calibration)
        {
            _explanation = explanation;
            _calibration = calibration;
        }

        public Task<ModelExplanationLoad> LoadExplanationAsync(string modelId, CancellationToken ct = default)
            => Task.FromResult(_explanation);

        public Task<ModelCalibrationLoad> LoadCalibrationAsync(string modelId, CancellationToken ct = default)
            => Task.FromResult(_calibration);
    }
}
