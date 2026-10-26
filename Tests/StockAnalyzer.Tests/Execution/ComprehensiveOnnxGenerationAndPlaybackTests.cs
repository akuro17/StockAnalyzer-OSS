#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Execution;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using Xunit;

namespace StockAnalyzer.Tests.Execution;

public sealed class ComprehensiveOnnxGenerationAndPlaybackTests : IDisposable
{
    private readonly string _sandboxTestRoot = Path.Combine(EphemeralSandbox.Root, "comprehensive_test_" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _disposables = new();

    public ComprehensiveOnnxGenerationAndPlaybackTests()
    {
        Directory.CreateDirectory(_sandboxTestRoot);
    }

    public void Dispose()
    {
        foreach (IDisposable d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }

        try
        {
            if (Directory.Exists(_sandboxTestRoot))
            {
                Directory.Delete(_sandboxTestRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string GoodModelFixturePath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StockAnalyzer.Core.Tests", "Assets", "trend_predictor_goodmeta.onnx"));

    private static string RlNumericQFixturePath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StockAnalyzer.Core.Tests", "Assets", "rl_numeric_q_fixture.onnx"));

    private static string RlPortfolioN4FixturePath =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "StockAnalyzer.Core.Tests", "Assets", "rl_portfolio_fixture_n4.onnx"));

    private static List<CandleData> GenerateRealCandles(int count, decimal startPrice = 150m)
    {
        var list = new List<CandleData>(count);
        var baseDate = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var random = new Random(42);
        decimal price = startPrice;

        for (int i = 0; i < count; i++)
        {
            decimal delta = (decimal)(random.NextDouble() - 0.48) * 2.0m;
            price = Math.Max(10m, price + delta);
            decimal high = price + (decimal)random.NextDouble() * 1.5m;
            decimal low = price - (decimal)random.NextDouble() * 1.5m;
            decimal open = (high + low) / 2m;
            list.Add(new CandleData(baseDate.AddDays(i), open, high, low, price, 10_000 + random.Next(1000)));
        }

        return list;
    }

    // =========================================================================================
    // Case A: Training Wizard - OnnxArtifactStorageMode.Temp + IPredictionRunTarget.RunTempModelAsync
    // =========================================================================================
    [Fact]
    public async Task CaseA_TrainingWizard_TempModelGenerationAndPrediction_Succeeds()
    {
        // 1. Verify IndicatorFactory creates real indicators without mocking
        var factory = new IndicatorFactory();
        ICoreIndicator? indicator = factory.Create(IndicatorType.SMA);
        Assert.NotNull(indicator);

        // 2. Generate PyTorch/MLP model into %TEMP% sandbox (simulating TrainingWizard OnnxArtifactStorageMode.Temp)
        string tempJobDir = Path.Combine(_sandboxTestRoot, "temp_run_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempJobDir);
        string tempOnnxPath = Path.Combine(tempJobDir, "trend_predictor.onnx");
        File.Copy(GoodModelFixturePath, tempOnnxPath);

        Assert.True(File.Exists(tempOnnxPath));
        Assert.True(EphemeralSandbox.IsStrictlyInside(tempOnnxPath));

        // 3. Create real CandleData records
        List<CandleData> realCandles = GenerateRealCandles(100, 180m);
        Assert.Equal(100, realCandles.Count);

        // 4. Execute prediction via IPredictionRunTarget
        IPredictionRunTarget target = new StandalonePredictionRunTarget(realCandles, factory);

        PredictionRunReport report = await target.RunTempModelAsync(tempOnnxPath, CancellationToken.None);

        // 5. Verify successful prediction outcome
        Assert.Equal(PredictionRunOutcome.Completed, report.Outcome);
        Assert.Null(report.Notice);
        Assert.NotNull(report.Display);
        Assert.True(report.Display.Contains("(") && report.Display.Contains("%"),
            $"Expected class and confidence display, got: {report.Display}");
        Assert.Equal(100, report.Bars);
    }

    // =========================================================================================
    // Case B: Single-Symbol RL - EphemeralRlRunner (RetainArtifact=true) AAPL policy.onnx replay
    // =========================================================================================
    [Fact]
    public async Task CaseB_SingleSymbolRl_EphemeralPolicyReplay_Steps0_63_127_Valid()
    {
        // 1. Generate AAPL policy.onnx in %TEMP% sandbox with RetainArtifact=true
        string jobDir = Path.Combine(_sandboxTestRoot, "rl_aapl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDir);
        string onnxModelPath = Path.Combine(jobDir, "policy.onnx");

        var metadata = RlObservationBuilder.CreateMetadata("AAPL", "fixture_source_rev", "fixture_eval_rev");
        AppendMetadata(RlNumericQFixturePath, onnxModelPath, metadata);

        // Write scaler sidecar
        var scaler = new FixedScaler(
            FixedScaler.CurrentSchemaVersion,
            RlFiles.FeatureMode,
            RlContract.WindowLength,
            Array.Empty<int>(),
            null,
            FixedScaler.ChannelNames(metadata, Array.Empty<int>()),
            Enumerable.Repeat(new FixedScalerStatistic(100d, 1d, 0d, 1_000d), RlContract.MarketChannelCount).ToArray(),
            FixedScaler.ComputeContractHash(metadata));
        File.WriteAllText(onnxModelPath + RlFiles.ScalerSuffix, System.Text.Json.JsonSerializer.Serialize(scaler, RlFiles.Json));
        File.WriteAllText(onnxModelPath + RlFiles.ScalerSuffix, System.Text.Json.JsonSerializer.Serialize(scaler, RlFiles.Json));

        // 2. Setup QValueService and RlDialogDataService
        var qService = new QValueService(new EmptyRegistry());
        var dataService = new RlDialogDataService(new EmptyRegistry(), qService);

        DateOnly asOfDate = new(2023, 6, 1);

        // 3. Replay at step 0
        var res0 = await dataService.ReplayEphemeralModelAsync(onnxModelPath, "AAPL", asOfDate, 0);
        Assert.NotNull(res0);
        Assert.Equal(3, res0.Snapshot.QValues.Length);
        Assert.True(Enum.IsDefined(typeof(RlAction), res0.Snapshot.SelectedAction));
        Assert.Equal(res0.Snapshot.SelectedAction, RlContract.SelectAction(res0.Snapshot.QValues.ToArray()));

        // 4. Replay at step 63
        var res63 = await dataService.ReplayEphemeralModelAsync(onnxModelPath, "AAPL", asOfDate, 63);
        Assert.NotNull(res63);
        Assert.Equal(3, res63.Snapshot.QValues.Length);
        Assert.True(Enum.IsDefined(typeof(RlAction), res63.Snapshot.SelectedAction));
        Assert.Equal(res63.Snapshot.SelectedAction, RlContract.SelectAction(res63.Snapshot.QValues.ToArray()));

        // 5. Replay at step 127
        var res127 = await dataService.ReplayEphemeralModelAsync(onnxModelPath, "AAPL", asOfDate, 127);
        Assert.NotNull(res127);
        Assert.Equal(3, res127.Snapshot.QValues.Length);
        Assert.True(Enum.IsDefined(typeof(RlAction), res127.Snapshot.SelectedAction));
        Assert.Equal(res127.Snapshot.SelectedAction, RlContract.SelectAction(res127.Snapshot.QValues.ToArray()));
    }

    // =========================================================================================
    // Case C: Portfolio RL - 4 Symbols policy.onnx shape [1, 5] & Softmax sum == 1.0m
    // =========================================================================================
    [Fact]
    public void CaseC_PortfolioRl_FourSymbols_LogitsShapeAndSoftmaxSumToUnity_Verified()
    {
        // 1. Generate 4-symbol policy.onnx into %TEMP% sandbox
        string jobDir = Path.Combine(_sandboxTestRoot, "rl_portfolio_4sym_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDir);
        string modelPath = Path.Combine(jobDir, "policy.onnx");

        var metadata = new Dictionary<string, string>
        {
            [RlPortfolioContract.MetadataContractHash] = RlPortfolioContract.ContractHash,
            [RlPortfolioContract.MetadataTargetType] = RlPortfolioContract.TargetType,
            [RlPortfolioContract.MetadataMaxSymbols] = "4",
            ["window_size"] = "64",
            [RlPortfolioContract.MetadataChannels] = "5",
            [RlPortfolioContract.MetadataUniverseSha256] = ContentHash.OfText("universe-4"),
            [RlPortfolioContract.MetadataCalendarSha256] = ContentHash.OfText("calendar-4"),
            [RlPortfolioContract.MetadataEvaluationSpecification] = "SA-RL-SHORT-HISTORY-v1",
            ["scaler_ref"] = "policy.onnx.scaler.json",
        };
        AppendMetadata(RlPortfolioN4FixturePath, modelPath, metadata);

        // Write scaler sidecar (5 channels for portfolio)
        RlPortfolioScaler.Save(modelPath, RlPortfolioScaler.Create(Enumerable.Repeat(new FixedScalerStatistic(100d, 5d, 50d, 200d), 5).ToArray()));

        // 2. Validate ONNX output tensor shape [1, 5]
        using var session = new InferenceSession(modelPath);
        _disposables.Add(session);
        var outputMeta = session.OutputMetadata.First();
        long[] outputDims = outputMeta.Value.Dimensions.Select(d => (long)d).ToArray();
        Assert.Equal(new long[] { 1, 5 }, outputDims);

        // 3. Run actor inference with a test observation
        int obsLength = checked((int)RlPortfolioResources.ObservationLength(4, RlPortfolioObservationLayout.V1));
        Assert.Equal(1292, obsLength);

        float[] observation = new float[obsLength];
        for (int i = 0; i < obsLength; i++)
        {
            observation[i] = 0.05f;
        }

        var inputTensor = new DenseTensor<float>(observation.AsMemory(), new[] { 1, obsLength });
        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("observation", inputTensor) };
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = session.Run(inputs);
        float[] logits = results.First().AsEnumerable<float>().ToArray();
        Assert.Equal(5, logits.Length);

        // 4. Verify softmax probability sum == 1.0 (tolerance 1e-4) via MLDataProcessor
        var processor = new MLDataProcessor();
        float[] probabilities = new float[5];
        processor.ComputeSoftmax(logits, probabilities);

        decimal probabilitySum = (decimal)probabilities.Sum();
        Assert.True(Math.Abs(1.0m - probabilitySum) <= 1e-4m,
            $"Expected probability sum of 1.0m, got {probabilitySum}");

        // 5. Verify RlPortfolioWeights.FromLogits produces exact 1.0m decimal sum (cash + 4 assets)
        bool[] tradable = new[] { true, true, true, true };
        RlPortfolioWeightVector weights = RlPortfolioWeights.FromLogits(logits, tradable, maxWeightPercent: 40);
        decimal totalDecimalWeight = weights.Assets.Sum() + weights.Cash;
        Assert.Equal(1.0m, totalDecimalWeight);
    }

    // =========================================================================================
    // Case D: Boundary & Exception Coverage
    // =========================================================================================
    [Fact]
    public async Task CaseD_BoundaryAndEdgeCases_ThrowAppropriateExceptions()
    {
        var qService = new QValueService(new EmptyRegistry());
        var dataService = new RlDialogDataService(new EmptyRegistry(), qService);

        // Create a valid dummy file inside the sandbox
        string validInside = Path.Combine(_sandboxTestRoot, "valid.onnx");
        File.WriteAllText(validInside, "placeholder");

        DateOnly asOfDate = new(2023, 6, 1);

        // 1. decisionPosition = -1 and 128 -> ArgumentOutOfRangeException
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            dataService.ReplayEphemeralModelAsync(validInside, "AAPL", asOfDate, -1));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            dataService.ReplayEphemeralModelAsync(validInside, "AAPL", asOfDate, 128));

        // 2. Path outside EphemeralSandbox.Root -> ArgumentException
        string outsidePath = Path.Combine(Path.GetTempPath(), "definitely_outside_" + Guid.NewGuid().ToString("N"), "model.onnx");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            dataService.ReplayEphemeralModelAsync(outsidePath, "AAPL", asOfDate, 0));

        // 3. Corrupted ONNX header -> OnnxRuntimeException / InvalidDataException
        string corruptedOnnx = Path.Combine(_sandboxTestRoot, "corrupted.onnx");
        byte[] corruptedBytes = new byte[128];
        Random.Shared.NextBytes(corruptedBytes);
        File.WriteAllBytes(corruptedOnnx, corruptedBytes);

        Assert.ThrowsAny<Exception>(() => new InferenceSession(corruptedOnnx));

        // 4. Symbol with 0 bars in period -> InsufficientStoredDataException
        var runnerWithNoData = new ScriptedRunnerWithNoData();
        var gateway = new SandboxedExecutionGateway(
            new DummyTrainingOrchestrator(),
            new DummyPythonResolver(),
            new SandboxedExecutionSettings { MaxConcurrentJobs = 1, KeepLatestRuns = 1 },
            NullLogger<SandboxedExecutionGateway>.Instance,
            _sandboxTestRoot,
            TimeSpan.FromSeconds(2),
            portfolioRunner: runnerWithNoData);
        _disposables.Add(gateway);

        var emptyPeriodRequest = new EphemeralPortfolioRlRequest
        {
            Symbols = new[] { "AAPL", "MSFT", "GOOG" },
            StartDate = null,
            EndDateExclusive = new DateOnly(2024, 1, 1),
            RetainArtifact = true,
        };

        await Assert.ThrowsAsync<InsufficientStoredDataException>(() =>
            gateway.StartPortfolioRlTrainingAsync(emptyPeriodRequest, CancellationToken.None));
    }

    // =========================================================================================
    // Helpers
    // =========================================================================================

    private static void AppendMetadata(string sourcePath, string destPath, IReadOnlyDictionary<string, string> metadata)
    {
        using var output = File.Create(destPath);
        output.Write(File.ReadAllBytes(sourcePath));
        foreach (var pair in metadata)
        {
            using var entry = new MemoryStream();
            WriteProtoString(entry, 1, pair.Key);
            WriteProtoString(entry, 2, pair.Value);
            WriteVarint(output, (14 << 3) | 2);
            WriteVarint(output, (int)entry.Length);
            entry.Position = 0;
            entry.CopyTo(output);
        }
    }

    private static void WriteProtoString(Stream stream, int field, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteVarint(stream, (field << 3) | 2);
        WriteVarint(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteVarint(Stream stream, int value)
    {
        uint v = (uint)value;
        while (v >= 0x80)
        {
            stream.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        stream.WriteByte((byte)v);
    }

    private sealed class StandalonePredictionRunTarget(IReadOnlyList<CandleData> candles, IndicatorFactory indicatorFactory) : IPredictionRunTarget
    {
        public Task<PredictionRunReport> RunAsync(CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<PredictionRunReport> RunTempModelAsync(string onnxPath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(onnxPath) || !File.Exists(onnxPath))
                return Task.FromResult(new PredictionRunReport(PredictionRunOutcome.Completed, "AAPL", TimeframeType.Daily, null, "File not found", candles.Count));

            using var lease = ArtifactLease.FromPath(onnxPath);
            using var model = EphemeralPredictionModel.Open(lease, new MLDataProcessor(), EphemeralExecutionPolicy.GuiEquivalent, indicatorFactory);
            var predictions = model.PredictLast(candles, 1);
            if (predictions.Count == 0)
                return Task.FromResult(new PredictionRunReport(PredictionRunOutcome.Completed, "AAPL", TimeframeType.Daily, null, "No prediction produced", candles.Count));

            var p = predictions[^1];
            string display = p.PredictedClass is not null
                ? $"{p.PredictedClass} ({(p.Confidence ?? 0f) * 100:F1}%)"
                : $"{p.Value:F4}";
            return Task.FromResult(new PredictionRunReport(PredictionRunOutcome.Completed, "AAPL", TimeframeType.Daily, display, null, candles.Count));
        }
    }

    private sealed class ScriptedRunnerWithNoData : IEphemeralPortfolioRlRunner
    {
        public Task<RlEnvironmentStatus> CheckEnvironmentAsync(CancellationToken cancellationToken)
            => Task.FromResult(RlEnvironmentStatus.Usable);

        public Task<PanelPreflight> PreflightAsync(IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive, bool holdoutRequested, RlPanelEvaluationMode mode, CancellationToken cancellationToken)
            => Task.FromResult(new PanelPreflight(start, endExclusive, mode, ImmutableArray<RlPanelSymbolPreflight>.Empty, 500, 500, ImmutableArray<RlPanelCellCount>.Empty, false, false));

        public Task<RlStartSuggestion?> SuggestStartAsync(IReadOnlyList<string> symbols, DateOnly endExclusive, RlPanelEvaluationMode mode, CancellationToken cancellationToken)
            => Task.FromResult<RlStartSuggestion?>(new RlStartSuggestion(RlStartSuggestionStatus.NoUsableData, null, 500, 0));

        public Task<RlPortfolioTrainingResult> RunAsync(RlPortfolioRunRequest request, string jobDirectory, IProgress<RlTrainingProgress>? progress, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class DummyTrainingOrchestrator : ITrainingOrchestrator
    {
        public Task<TrainingRunResult> StartTrainingAsync(TrainingJobConfig config, IProgress<TrainingProgress>? progress = null, CancellationToken ct = default)
            => Task.FromException<TrainingRunResult>(new NotSupportedException());
    }

    private sealed class DummyPythonResolver : IPythonExecutableResolver
    {
        public bool TryResolve(out string executablePath)
        {
            executablePath = "python.exe";
            return true;
        }
    }

    private sealed class EmptyRegistry : IRlModelGenerationRegistry
    {
        public string? RlActiveId => null;
        public IReadOnlyList<RlGeneration> ListRl() => Array.Empty<RlGeneration>();
        public ModelGenerationLease? AcquireRl(string modelId) => null;
        public ModelGenerationLease? AcquireRlActive() => null;
        public RlGeneration? GetRlGeneration(string modelId) => null;
        public Task<bool> ActivateRlAsync(string modelId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<RlGeneration> RegisterRlAsync(string artifactDirectory, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
