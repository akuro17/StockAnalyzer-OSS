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

    [Theory]
    [InlineData(3000000000L)]
    [InlineData(2147483647L)]
    [InlineData(9223372036854775807L)]
    public async Task AnOffsetBeyondTheIntegerRange_IsAnEmptyPageNotAnInternalError(long offset)
    {
        string modelId = "test_list_big_offset";
        string modelSubDir = Path.Combine(_modelDir, modelId);
        Directory.CreateDirectory(modelSubDir);
        File.WriteAllText(Path.Combine(modelSubDir, "manifest.json"), "{\"schemaVersion\":1,\"modelId\":\"test_list_big_offset\",\"modelFile\":\"m.onnx\",\"modelHash\":\"a\",\"requiredSidecars\":[],\"contractHash\":\"b\",\"evaluationRevision\":\"c\",\"targetType\":\"classification\",\"timeframe\":\"daily\",\"horizon\":5,\"classOrder\":\"Up,Down\",\"score\":0.5}");
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var tool = new ListModelsTool(new SymbolResolver(new EmptyReadOnlyStockAnalyzerGateway()), registry, new McpLimits());

        using var doc = JsonDocument.Parse($"{{\"offset\":{offset}}}");
        string json = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);
        using var res = JsonDocument.Parse(json);
        var root = res.RootElement;

        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(offset, root.GetProperty("offset").GetInt64());
        Assert.Equal(0, root.GetProperty("returnedCount").GetInt32());
        Assert.False(root.GetProperty("hasMore").GetBoolean());
        Assert.Empty(root.GetProperty("models").EnumerateArray());
    }

    // ---- provenance: createdAt, createdAtSource, trainingMode / architecture (plan appendix R-2, R-3) ----

    private void WriteSupervised(string id, string? runId, string mode = "fresh", DateTime? creationUtc = null, DateTime? lastWriteUtc = null)
    {
        string dir = Path.Combine(_modelDir, id);
        Directory.CreateDirectory(dir);
        var manifest = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["modelId"] = id,
            ["modelFile"] = "model.onnx",
            ["modelHash"] = "a",
            ["requiredSidecars"] = Array.Empty<object>(),
            ["contractHash"] = "b",
            ["evaluationRevision"] = "c",
            ["targetType"] = "classification",
            ["timeframe"] = "daily",
            ["horizon"] = 5,
            ["classOrder"] = "Up,Down",
            ["score"] = 0.5,
        };
        if (runId is not null)
        {
            manifest["trainingLineage"] = new { mode, runId, parentModelId = (string?)null, parentCheckpointSha256 = (string?)null, parentModelSha256 = (string?)null };
        }

        string path = Path.Combine(dir, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        if (creationUtc.HasValue) File.SetCreationTimeUtc(path, creationUtc.Value);
        if (lastWriteUtc.HasValue) File.SetLastWriteTimeUtc(path, lastWriteUtc.Value);
    }

    private void WriteReinforcement(string id, DateTimeOffset createdUtc)
    {
        string dir = Path.Combine(_rlDir, id);
        Directory.CreateDirectory(dir);
        // The reinforcement-learning manifest is read with snake_case names (RlFiles.Json).
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model_id"] = id,
            ["symbol"] = "7203-T",
            ["source_revision"] = "rev1",
            ["contract_hash"] = "hash1",
            ["evaluation_revision"] = "rev_eval",
            ["validation_return"] = 15.5m,
            ["oos_return"] = 12.3m,
            ["files"] = new Dictionary<string, string>(),
            ["created_utc"] = createdUtc,
        }));
    }

    /// <summary>A trainer-style metrics sidecar of a supervised generation; the generation lists its symbols and may carry a <c>created_utc</c> of its own.</summary>
    private void WriteMetrics(string id, string? createdUtc)
    {
        var metrics = new Dictionary<string, object?> { ["window_size"] = 60, ["symbols"] = new[] { "7203-T" } };
        if (createdUtc is not null)
        {
            metrics["created_utc"] = createdUtc;
        }

        File.WriteAllText(Path.Combine(_modelDir, id, "model.onnx.metrics.json"), JsonSerializer.Serialize(metrics));
    }

    private async Task<List<JsonElement>> ListAsync()
    {
        var tool = new ListModelsTool(new SymbolResolver(new EmptyReadOnlyStockAnalyzerGateway()), new ReadOnlyModelGenerationRegistry(_modelDir), new McpLimits());
        using var doc = JsonDocument.Parse("{}");
        string json = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);
        return JsonDocument.Parse(json).RootElement.Clone().GetProperty("models").EnumerateArray().ToList();
    }

    [Fact]
    public async Task CreatedAtComesFromTheRunIdAndNotFromTheFileTimes_InUtcWithFixedMilliseconds()
    {
        // The file times are set so that they say the opposite of the run ids: copying a generation must not change the order.
        WriteSupervised("gen_a", "20260901-102030123-aaaaaaaa", creationUtc: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteSupervised("gen_b", "20261015-081500000-bbbbbbbb", creationUtc: new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        List<JsonElement> models = await ListAsync();

        Assert.Equal(new[] { "gen_b", "gen_a" }, models.Select(m => m.GetProperty("modelId").GetString()));
        Assert.Equal("2026-10-15T08:15:00.000Z", models[0].GetProperty("createdAt").GetString());   // a zero millisecond keeps ".000"
        Assert.Equal("2026-09-01T10:20:30.123Z", models[1].GetProperty("createdAt").GetString());
        Assert.All(models, m => Assert.Equal("runId", m.GetProperty("createdAtSource").GetString()));
    }

    [Fact]
    public async Task AGenerationWithoutARunIdUsesTheOlderOfCreationAndLastWriteTimeAndSaysSo()
    {
        // A copy keeps the last write time but gets a new creation time: the older of the two is the better guess.
        WriteSupervised("legacy_copied", null,
            creationUtc: new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            lastWriteUtc: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        // A generation that was never copied: creation first, written a day later, with sub-millisecond ticks that must be cut, not rounded.
        WriteSupervised("legacy_native", null,
            creationUtc: new DateTime(2026, 3, 1, 12, 0, 0, 500, DateTimeKind.Utc).AddTicks(9999),
            lastWriteUtc: new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc));

        List<JsonElement> models = await ListAsync();

        JsonElement copied = models.Single(m => m.GetProperty("modelId").GetString() == "legacy_copied");
        JsonElement native = models.Single(m => m.GetProperty("modelId").GetString() == "legacy_native");
        Assert.Equal("2026-05-01T00:00:00.000Z", copied.GetProperty("createdAt").GetString());
        Assert.Equal("2026-03-01T12:00:00.500Z", native.GetProperty("createdAt").GetString());
        Assert.Equal("filesystem", copied.GetProperty("createdAtSource").GetString());
        Assert.Equal("filesystem", native.GetProperty("createdAtSource").GetString());
    }

    [Fact]
    public async Task ACreatedUtcInTheMetricsSidecarDoesNotOverrideTheRunIdTime()
    {
        // The label says runId, so the time must be the one of the run id; a stray value in a sidecar must not change it (and would not be cut to the millisecond).
        WriteSupervised("gen_a", "20260901-102030123-aaaaaaaa");
        WriteMetrics("gen_a", "2001-02-03T04:05:06.7891234+00:00");

        JsonElement model = Assert.Single(await ListAsync());

        Assert.Equal("2026-09-01T10:20:30.123Z", model.GetProperty("createdAt").GetString());
        Assert.Equal("runId", model.GetProperty("createdAtSource").GetString());
        Assert.Equal("7203-T", Assert.Single(model.GetProperty("symbols").EnumerateArray()).GetString());   // the rest of the sidecar is still read
    }

    [Fact]
    public async Task ACreatedUtcInTheMetricsSidecarDoesNotOverrideTheFileTimesOfAGenerationWithoutARunId()
    {
        WriteSupervised("legacy", null,
            creationUtc: new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc),
            lastWriteUtc: new DateTime(2026, 3, 2, 12, 0, 0, DateTimeKind.Utc));
        WriteMetrics("legacy", "2001-02-03T04:05:06+00:00");

        JsonElement model = Assert.Single(await ListAsync());

        Assert.Equal("2026-03-01T12:00:00.000Z", model.GetProperty("createdAt").GetString());
        Assert.Equal("filesystem", model.GetProperty("createdAtSource").GetString());
    }

    [Fact]
    public async Task EveryCreatedAtIsTwentyFourCharactersOfUtc_SoTheStringOrderIsTheListOrder()
    {
        WriteSupervised("gen_a", "20260901-102030123-aaaaaaaa");
        WriteSupervised("gen_b", "20261015-081500000-bbbbbbbb");
        WriteSupervised("legacy", null, creationUtc: new DateTime(2026, 7, 4, 1, 2, 3, DateTimeKind.Utc), lastWriteUtc: new DateTime(2026, 7, 4, 1, 2, 3, DateTimeKind.Utc));
        WriteReinforcement("rl_a", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(9)));

        List<JsonElement> models = await ListAsync();
        string[] stamps = models.Select(m => m.GetProperty("createdAt").GetString()!).ToArray();

        Assert.Equal(4, stamps.Length);
        Assert.All(stamps, s =>
        {
            Assert.Equal(24, s.Length);
            Assert.EndsWith("Z", s, StringComparison.Ordinal);
        });
        Assert.Equal(stamps.OrderByDescending(s => s, StringComparer.Ordinal), stamps);
        Assert.Equal("2026-10-31T15:00:00.000Z", stamps[0]);   // the RL record is written with +09:00 and is shown in UTC
    }

    [Fact]
    public async Task ARecordedReinforcementLearningTimeIsShownInUtcAndCutToTheMillisecond()
    {
        WriteReinforcement("rl_a", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(9999));

        List<JsonElement> models = await ListAsync();

        Assert.Equal("2026-11-01T00:00:00.000Z", models.Single().GetProperty("createdAt").GetString());
        Assert.Equal("recorded", models.Single().GetProperty("createdAtSource").GetString());
    }

    [Fact]
    public async Task TheTrainingModeIsSupervisedOnly_TheArchitectureIsReinforcementLearningOnly_AndNotApplicableKeysAreOmitted()
    {
        WriteSupervised("gen_fresh", "20260901-102030123-aaaaaaaa", mode: "fresh");
        WriteSupervised("gen_tuned", "20260902-102030123-bbbbbbbb", mode: "fine_tune");
        WriteSupervised("legacy", null);
        WriteReinforcement("rl_a", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));

        List<JsonElement> models = await ListAsync();
        JsonElement Contract(string id) => models.Single(m => m.GetProperty("modelId").GetString() == id).GetProperty("contract");

        Assert.Equal("fresh", Contract("gen_fresh").GetProperty("trainingMode").GetString());
        Assert.Equal("fine_tune", Contract("gen_tuned").GetProperty("trainingMode").GetString());
        Assert.False(Contract("gen_fresh").TryGetProperty("architecture", out _));
        Assert.False(Contract("gen_tuned").TryGetProperty("architecture", out _));

        Assert.False(Contract("legacy").TryGetProperty("trainingMode", out _));      // unknown: omitted, not null
        Assert.False(Contract("legacy").TryGetProperty("architecture", out _));

        Assert.Equal("PPO", Contract("rl_a").GetProperty("architecture").GetString());
        Assert.False(Contract("rl_a").TryGetProperty("trainingMode", out _));
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
