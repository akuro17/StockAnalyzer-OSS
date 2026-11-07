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

    // ---- regime: the model's own time frame and the reason a regime is absent (plan appendix R-4) ----

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

    private static ModelAnalysisSnapshot Snapshot(string modelId, string timeframe, string regimeStatus = "available", ImmutableArray<AnalysisPeriod>? periods = null)
    {
        var regime = new AnalysisRegime(
            regimeStatus, "kmeans", 3, "k-means++", 10, 42, 300, 1e-4, "lloyd", ModelAnalysisContract.VolatilityBars, 0,
            ImmutableArray.Create("return", "volatility"), 100, ImmutableArray<AnalysisPeriod>.Empty, "rev",
            Mean: ImmutableArray.Create(0.0, 0.01),
            Std: ImmutableArray.Create(0.01, 0.01),
            Centroids: ImmutableArray.Create(
                ImmutableArray.Create(-1.0, -1.0),
                ImmutableArray.Create(0.0, 0.0),
                ImmutableArray.Create(1.0, 1.0)));
        var importance = new AnalysisImportance("available", "permutation", "accuracy", "validation", ImmutableArray<ChannelImportance>.Empty);
        var analysis = new ModelAnalysis(
            1, new string('a', 64), new string('b', 64), new string('c', 64), timeframe, 20,
            ImmutableArray.Create("lag0:close"), ImmutableArray.Create("close"), periods ?? ImmutableArray<AnalysisPeriod>.Empty, regime, importance);
        return new ModelAnalysisSnapshot(modelId, analysis);
    }

    private static List<CandleData> Bars(int count)
    {
        var list = new List<CandleData>();
        for (int i = 0; i < count; i++)
        {
            decimal close = 100m + (i % 7) * 1.5m + i * 0.3m;
            list.Add(new CandleData(new DateTime(2025, 1, 6).AddDays(7 * i), close, close + 1m, close - 1m, close, 1000));
        }
        return list;
    }

    private async Task<(JsonElement Root, FrameGateway Gateway, ProbeAnalysisService Analysis)> RunRegimeAsync(
        string manifestTimeframe, ModelAnalysisSnapshot? snapshot, int bars, RegimeAssignment? fixedAssignment, bool realAssign, string extraArguments = ",\"symbol\":\"7203-T\"", Exception? loadError = null)
    {
        string modelId = "test_regime_" + Guid.NewGuid().ToString("N")[..8];
        WriteManifest(modelId, manifestTimeframe);
        var registry = new ReadOnlyModelGenerationRegistry(_modelDir);
        var gateway = new FrameGateway(bars);
        var analysis = new ProbeAnalysisService(
            snapshot is null ? null : snapshot with { ModelId = modelId },
            fixedAssignment,
            realAssign ? new ModelAnalysisService(registry, new MLDataProcessor()) : null,
            loadError);
        var explain = new MockExplainabilityService(
            new ModelExplanationLoad(ExplanationAvailability.Available, CreateDummyExplanation()),
            new ModelCalibrationLoad(CalibrationAvailability.Active, CreateDummyCalibration()));
        var tool = new GetModelExplanationTool(gateway, new SymbolResolver(gateway), registry, explainabilityService: explain, analysisService: analysis);

        using var doc = JsonDocument.Parse($"{{\"modelId\":\"{modelId}\"{extraArguments}}}");
        string json = await tool.InvokeAsync(doc.RootElement, CancellationToken.None);
        return (JsonDocument.Parse(json).RootElement.Clone(), gateway, analysis);
    }

    [Theory]
    [InlineData("daily", "Daily", TimeFrame.D1, TimeframeType.Daily)]
    [InlineData("weekly", "Weekly", TimeFrame.W1, TimeframeType.Weekly)]
    [InlineData("monthly", "Monthly", TimeFrame.MN1, TimeframeType.Monthly)]
    public async Task TheRegimeIsAssignedOnBarsOfTheModelsOwnTimeFrame_AndItsReasonIsNull(
        string manifestTimeframe, string analysisTimeframe, TimeFrame expectedFrame, TimeframeType expectedType)
    {
        var (root, gateway, analysis) = await RunRegimeAsync(manifestTimeframe, Snapshot("x", analysisTimeframe), 30, null, realAssign: true);

        Assert.Equal(new[] { (expectedFrame, false) }, gateway.Requests);
        Assert.Equal(new[] { expectedType }, analysis.AssignedFrames);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("regime").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("regimeReason").ValueKind);
    }

    [Fact]
    public async Task AnAsOfDateReadsTheModelsTimeFrameWithTheAsOfOverload()
    {
        var (_, gateway, _) = await RunRegimeAsync("weekly", Snapshot("x", "Weekly"), 30, null, realAssign: true, ",\"symbol\":\"7203-T\",\"asOfDate\":\"2026-01-09\"");

        Assert.Equal(new[] { (TimeFrame.W1, true) }, gateway.Requests);
    }

    [Fact]
    public async Task WithoutASymbolThereIsNoRegimeAndNoRegimeReason()
    {
        var (root, gateway, _) = await RunRegimeAsync("weekly", Snapshot("x", "Weekly"), 30, null, realAssign: true, "");

        Assert.False(root.TryGetProperty("regime", out _));
        Assert.False(root.TryGetProperty("regimeReason", out _));
        Assert.Empty(gateway.Requests);
    }

    public static TheoryData<string, string?, string, int, string> ReasonCases => new()
    {
        // manifest time frame, snapshot time frame (null = no snapshot), regime status, bars, expected reason
        { "weekly", null, "available", 30, "analysis_unavailable" },
        { "weekly", "Weekly", "unavailable", 30, "analysis_unavailable" },
        { "weekly", "Daily", "available", 30, "timeframe_mismatch" },
        { "hourly", "Daily", "available", 30, "timeframe_mismatch" },
        { "weekly", "Weekly", "available", 20, "insufficient_bars" },
        { "weekly", "Weekly", "available", 5, "insufficient_bars" },
        // two reasons at once: the first in the order analysis_unavailable -> timeframe_mismatch -> insufficient_bars wins
        { "weekly", "Daily", "unavailable", 30, "analysis_unavailable" },
        { "weekly", "Daily", "available", 5, "timeframe_mismatch" },
        { "weekly", null, "available", 5, "analysis_unavailable" },
    };

    [Theory]
    [MemberData(nameof(ReasonCases))]
    public async Task ARegimeThatCannotBeAssignedNamesTheFirstReasonInTheFixedOrder_AndNoUnneededBarIsRead(
        string manifestTimeframe, string? snapshotTimeframe, string regimeStatus, int bars, string expectedReason)
    {
        ModelAnalysisSnapshot? snapshot = snapshotTimeframe is null ? null : Snapshot("x", snapshotTimeframe, regimeStatus);

        var (root, gateway, analysis) = await RunRegimeAsync(manifestTimeframe, snapshot, bars, null, realAssign: true);

        Assert.Equal(JsonValueKind.Null, root.GetProperty("regime").ValueKind);
        Assert.Equal(expectedReason, root.GetProperty("regimeReason").GetString());
        Assert.Equal(0, analysis.AssignCalls);
        if (expectedReason != "insufficient_bars")
        {
            Assert.Empty(gateway.Requests); // the first two reasons are known before any bar is read
        }

        // The explanation itself is unaffected by the missing regime.
        Assert.Equal("Available", root.GetProperty("explanation").GetProperty("availability").GetString());
    }

    [Fact]
    public async Task WhenTheCoreAssignmentStillReturnsNothingTheReasonIsNotAssignable()
    {
        var (root, _, analysis) = await RunRegimeAsync("weekly", Snapshot("x", "Weekly"), 30, fixedAssignment: null, realAssign: false);

        Assert.Equal(1, analysis.AssignCalls);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("regime").ValueKind);
        Assert.Equal("not_assignable", root.GetProperty("regimeReason").GetString());
    }

    [Theory]
    [InlineData("weekly", null, "available", 30)]
    [InlineData("weekly", "Weekly", "unavailable", 30)]
    [InlineData("weekly", "Daily", "available", 30)]
    [InlineData("weekly", "Weekly", "available", 20)]
    public async Task EveryPreGatedReasonAgreesWithTheCoreAssignmentReturningNothing(string manifestTimeframe, string? snapshotTimeframe, string regimeStatus, int bars)
    {
        // The reasons duplicate three of the checks inside ModelAnalysisService.Assign; if they drift apart this fails.
        ModelAnalysisSnapshot snapshot = Snapshot("x", snapshotTimeframe ?? "Weekly", snapshotTimeframe is null ? "unavailable" : regimeStatus);
        var core = new ModelAnalysisService(new ReadOnlyModelGenerationRegistry(_modelDir), new MLDataProcessor());

        Assert.Null(core.Assign(snapshot, Bars(bars), TimeframeType.Weekly));

        var (root, _, _) = await RunRegimeAsync(manifestTimeframe, snapshotTimeframe is null ? null : snapshot, bars, null, realAssign: true);
        Assert.NotEqual(JsonValueKind.Object, root.GetProperty("regime").ValueKind);
    }

    // ---- pointInTime: the explanation is a fixed artefact of the training run (plan appendix R-1) ----

    private static ImmutableArray<AnalysisPeriod> Periods(params (string Symbol, string Kind, string End)[] rows)
        => rows.Select(r =>
        {
            DateTime end = DateTime.Parse(r.End, System.Globalization.CultureInfo.InvariantCulture);
            return new AnalysisPeriod(r.Symbol, r.Kind, end.AddDays(-30), end, 100, "rev");
        }).ToImmutableArray();

    private static ModelAnalysisSnapshot SnapshotEndingOn20251231() => Snapshot("x", "Daily", periods: Periods(
        ("MSFT", "train", "2025-06-30"), ("MSFT", "validation", "2025-09-30"), ("MSFT", "oos", "2025-12-31"),
        ("7203-T", "train", "2025-03-31"), ("7203-T", "validation", "2025-05-31")));

    [Fact]
    public async Task PointInTimeStatesThatTheExplanationIsStaticAndGivesTheLastAnchorOfAllSymbolsAndTheHorizon_EvenWithoutASymbol()
    {
        var (root, gateway, _) = await RunRegimeAsync("daily", SnapshotEndingOn20251231(), 30, null, realAssign: true, "");

        JsonElement pit = root.GetProperty("pointInTime");
        Assert.True(pit.GetProperty("explanationIsStatic").GetBoolean());
        Assert.Equal("2025-12-31", pit.GetProperty("dataEnd").GetString());
        Assert.Equal(5, pit.GetProperty("horizonBars").GetInt32());
        Assert.False(pit.TryGetProperty("asOfBeforeDataEnd", out _));   // no asOfDate, nothing to compare
        Assert.Empty(gateway.Requests);                                  // reading the sidecar needs no bars
    }

    [Theory]
    [InlineData("2025-12-30", true)]
    [InlineData("2025-12-31", false)]   // the day of the last anchor is not before it
    [InlineData("2026-01-01", false)]
    public async Task AsOfBeforeDataEndIsTheStrictComparisonAsOfDateLessThanDataEnd(string asOfDate, bool expected)
    {
        var (root, _, _) = await RunRegimeAsync("daily", SnapshotEndingOn20251231(), 30, null, realAssign: true, $",\"symbol\":\"7203-T\",\"asOfDate\":\"{asOfDate}\"");

        JsonElement pit = root.GetProperty("pointInTime");
        Assert.Equal(expected, pit.GetProperty("asOfBeforeDataEnd").GetBoolean());
        Assert.Equal("2025-12-31", pit.GetProperty("dataEnd").GetString());
    }

    [Fact]
    public async Task WithoutAnAnalysisSidecarDataEndAndTheComparisonAreNullNeverFalse_AndTheHorizonIsStillGiven()
    {
        var (root, _, _) = await RunRegimeAsync("daily", null, 30, null, realAssign: true, ",\"symbol\":\"7203-T\",\"asOfDate\":\"2020-01-01\"");

        JsonElement pit = root.GetProperty("pointInTime");
        Assert.True(pit.GetProperty("explanationIsStatic").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pit.GetProperty("dataEnd").ValueKind);
        Assert.Equal(JsonValueKind.Null, pit.GetProperty("asOfBeforeDataEnd").ValueKind);
        Assert.Equal(5, pit.GetProperty("horizonBars").GetInt32());
        Assert.Equal("Available", root.GetProperty("explanation").GetProperty("availability").GetString());
    }

    [Fact]
    public async Task ASnapshotWithoutPeriodsAlsoGivesNullDataEnd()
    {
        var (root, _, _) = await RunRegimeAsync("daily", Snapshot("x", "Daily"), 30, null, realAssign: true, ",\"symbol\":\"7203-T\",\"asOfDate\":\"2020-01-01\"");

        Assert.Equal(JsonValueKind.Null, root.GetProperty("pointInTime").GetProperty("dataEnd").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("pointInTime").GetProperty("asOfBeforeDataEnd").ValueKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"symbol\":\"7203-T\"")]
    public async Task AnAnalysisSidecarThatCannotBeLoadedNeverStopsTheToolAndLeaksNoText(string extraArguments)
    {
        var failure = new InvalidDataException("cannot read C:\\secret\\model.onnx.analysis.json");

        var (root, _, _) = await RunRegimeAsync("daily", null, 30, null, realAssign: true, extraArguments, loadError: failure);

        Assert.Equal(JsonValueKind.Null, root.GetProperty("pointInTime").GetProperty("dataEnd").ValueKind);
        Assert.Equal("Available", root.GetProperty("explanation").GetProperty("availability").GetString());
        Assert.DoesNotContain("secret", root.ToString(), StringComparison.OrdinalIgnoreCase);
        if (extraArguments.Length > 0)
        {
            Assert.Equal("analysis_unavailable", root.GetProperty("regimeReason").GetString());
        }
    }

    [Fact]
    public async Task CancellationDuringTheSidecarLoadIsNotSwallowed()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => RunRegimeAsync("daily", null, 30, null, realAssign: true, "", loadError: new OperationCanceledException()));
    }

    private sealed class FrameGateway : EmptyReadOnlyStockAnalyzerGateway
    {
        private readonly int _bars;

        public FrameGateway(int bars) => _bars = bars;

        public List<(TimeFrame Frame, bool AsOf)> Requests { get; } = new();

        public override Task<IReadOnlyList<string>> GetAvailableTickersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(new[] { "7203-T" });

        public override Task<IReadOnlyList<CandleData>> GetCandlesAsync(string symbol, TimeFrame timeFrame, int count, CancellationToken cancellationToken = default)
        {
            Requests.Add((timeFrame, false));
            return Task.FromResult<IReadOnlyList<CandleData>>(Bars(Math.Min(count, _bars)));
        }

        public override Task<IReadOnlyList<CandleData>> GetCandlesUpToAsync(string symbol, TimeFrame timeFrame, DateOnly endDate, int count, CancellationToken cancellationToken = default)
        {
            Requests.Add((timeFrame, true));
            return Task.FromResult<IReadOnlyList<CandleData>>(Bars(Math.Min(count, _bars)));
        }
    }

    private sealed class ProbeAnalysisService : IModelAnalysisService
    {
        private readonly ModelAnalysisSnapshot? _snapshot;
        private readonly RegimeAssignment? _fixed;
        private readonly ModelAnalysisService? _core;

        private readonly Exception? _loadError;

        public ProbeAnalysisService(ModelAnalysisSnapshot? snapshot, RegimeAssignment? fixedAssignment, ModelAnalysisService? core, Exception? loadError = null)
        {
            _snapshot = snapshot;
            _fixed = fixedAssignment;
            _core = core;
            _loadError = loadError;
        }

        public int AssignCalls { get; private set; }

        public List<TimeframeType> AssignedFrames { get; } = new();

        public Task<ModelAnalysisSnapshot?> LoadAsync(string modelId, CancellationToken ct = default)
            => _loadError is null ? Task.FromResult(_snapshot) : Task.FromException<ModelAnalysisSnapshot?>(_loadError);

        public RegimeAssignment? Assign(ModelAnalysisSnapshot snapshot, IReadOnlyList<CandleData> candles, TimeframeType timeframe)
        {
            AssignCalls++;
            AssignedFrames.Add(timeframe);
            return _core is not null ? _core.Assign(snapshot, candles, timeframe) : _fixed;
        }
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
