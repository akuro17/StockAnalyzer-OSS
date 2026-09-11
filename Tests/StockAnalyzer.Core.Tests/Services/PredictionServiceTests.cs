using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Tests.Services;

[Collection("Non-Parallel ONNX Tests")]
public class PredictionServiceTests
{
    private class TestSettings : IStockAnalyzerSettings
    {
        private readonly string _predictionModelPath;
        private readonly int _predictionWindowSize;
        private readonly PredictionFeatureMode _predictionFeatureMode;

        public TestSettings(
            string predictionModelPath = "NonExistent/does_not_exist.onnx",
            int predictionWindowSize = 3,
            PredictionFeatureMode predictionFeatureMode = PredictionFeatureMode.OhlcvMinMax)
        {
            _predictionModelPath = predictionModelPath;
            _predictionWindowSize = predictionWindowSize;
            _predictionFeatureMode = predictionFeatureMode;
        }

        public string? PythonPath => null;
        public string PythonScriptDirectory => "";
        public string PythonServerScriptName => "";
        public int PythonMaxRetries => 3;
        public int PythonBackoffMs => 1;
        public int PythonHealthCheckIntervalMs => 100000;
        public int PipeConnectPollIntervalMs => 100;
        public int SyncTimeoutMinutes => 1;
        public IReadOnlyList<string> PythonEssentialPackages => new List<string>();
        public int DisposeWaitMs => 100;
        public string DefaultSymbol => "MSFT";
        public string RenkoUpColor => "#00FF00";
        public string RenkoDownColor => "#FF0000";
        public string KagiUpColor => "#00FF00";
        public string KagiDownColor => "#FF0000";
        public string PnfUpColor => "#00FF00";
        public string PnfDownColor => "#FF0000";
        public string GetReverseWatchPhaseColor(int phase) => "#FFFFFF";
        public string? ScreeningDataPath => null;
        public IReadOnlyList<string> DefaultScreenerSymbols => new List<string>();
        public string PipeName => "predictiontestpipe";
        public int PipeConnectionTimeoutMs => 5000;
        public int ScreenerMaxParallelism => 4;
        public decimal ZigzagThresholdPercent => 5m;
        public int PatternRecognitionMinWindow => 10;
        public int PatternRecognitionMaxWindow => 100;
        public int PatternRecognitionWindowStep => 5;
        public double PatternRecognitionDefaultThreshold => 0.5;
        public int CircuitBreakerMinimumThroughput => 2;
        public double CircuitBreakerFailureRatio => 0.5;
        public int CircuitBreakerBreakDurationMs => 30000;
        public int CircuitBreakerSamplingDurationMs => 60000;
        public string PredictionModelPath => _predictionModelPath;
        public int PredictionWindowSize => _predictionWindowSize;
        public PredictionFeatureMode PredictionFeatureMode => _predictionFeatureMode;
        public float PredictionConfidenceThreshold => 0.5f;
        public string? PredictionInputNodeName => null;
        public string? PredictionOutputNodeName => null;
        public IReadOnlyList<string> PredictionClassLabels => new[] { "Up", "Down", "Neutral" };
        public int PredictionRetryMaxAttempts => 1;
        public int PredictionRetryBaseDelayMs => 1;
        public int PredictionRetryMaxDelayMs => 1;
        public string? LocaleResourcePath => null;
    }

    private static List<CandleData> BuildCandles(int count)
    {
        var candles = new List<CandleData>();
        for (int i = 0; i < count; i++)
        {
            candles.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(i), 100m, 105m, 95m, 100m + i, 1000));
        }
        return candles;
    }

    [Fact]
    public async Task PredictAsync_InsufficientData_ReturnsEmptyImmediately()
    {
        var service = new PredictionService(new TestSettings(), new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(1));

        Assert.Equal(PredictionResult.Empty, result);
        Assert.True(result.IsFallback);
    }

    [Fact]
    public async Task PredictAsync_ModelLoadFailsRepeatedly_TripsCircuitBreakerAndReturnsFallback()
    {
        var service = new PredictionService(new TestSettings(), new MLDataProcessor());
        var candles = BuildCandles(5);

        PredictionResult? lastResult = null;
        for (int i = 0; i < 3; i++)
        {
            lastResult = await service.PredictAsync(candles);
        }

        Assert.NotNull(lastResult);
        Assert.True(lastResult!.IsFallback);
        Assert.Equal("Unknown", lastResult.Label);
    }

    [Fact]
    public async Task PredictAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        var service = new PredictionService(new TestSettings(), new MLDataProcessor());
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.PredictAsync(BuildCandles(5)));
    }

    [Fact]
    public void ResolveNodeName_SingleNode_AdoptsSoleNode()
    {
        var resolved = PredictionService.ResolveNodeName(new[] { "only_node" }, configuredName: null);
        Assert.Equal("only_node", resolved);
    }

    [Fact]
    public void ResolveNodeName_MultipleNodes_SelectsOrdinalSortedFirstDeterministically()
    {
        var resolved = PredictionService.ResolveNodeName(new[] { "zeta", "alpha", "middle" }, configuredName: null);
        Assert.Equal("alpha", resolved);
    }

    [Fact]
    public void ResolveNodeName_ConfiguredName_TakesPrecedence()
    {
        var resolved = PredictionService.ResolveNodeName(new[] { "alpha", "beta" }, configuredName: "beta");
        Assert.Equal("beta", resolved);
    }

    [Fact]
    public void ValidateModelContract_ConformantShapes_DoesNotThrow()
    {
        PredictionService.ValidateModelContract(
            inputIsTensor: true, inputDimensions: new[] { 1, 3, 5 },
            outputIsTensor: true, outputDimensions: new[] { 1, 3 },
            expectedWindowSize: 3, expectedFeaturesPerBar: 5, expectedClassCount: 3,
            inputNodeName: "input", outputNodeName: "output");
    }

    [Fact]
    public void ValidateModelContract_DynamicDimensions_DoesNotThrow()
    {
        PredictionService.ValidateModelContract(
            inputIsTensor: true, inputDimensions: new[] { -1, -1, -1 },
            outputIsTensor: true, outputDimensions: new[] { -1, -1 },
            expectedWindowSize: 10, expectedFeaturesPerBar: 5, expectedClassCount: 3,
            inputNodeName: "input", outputNodeName: "output");
    }

    [Fact]
    public void ValidateModelContract_AbsentDimensionMetadata_DoesNotThrow()
    {
        PredictionService.ValidateModelContract(
            inputIsTensor: true, inputDimensions: null,
            outputIsTensor: true, outputDimensions: System.Array.Empty<int>(),
            expectedWindowSize: 10, expectedFeaturesPerBar: 5, expectedClassCount: 3,
            inputNodeName: "input", outputNodeName: "output");
    }

    public static IEnumerable<object[]> ContractViolationShapes()
    {
        // input rank != 3
        yield return new object[] { new[] { 1, 3 }, new[] { 1, 3 } };
        // input sequence dimension != WindowSize (3)
        yield return new object[] { new[] { 1, 7, 5 }, new[] { 1, 3 } };
        // input feature dimension != FeaturesPerBar (5)
        yield return new object[] { new[] { 1, 3, 4 }, new[] { 1, 3 } };
        // output rank != 2
        yield return new object[] { new[] { 1, 3, 5 }, new[] { 1, 3, 1 } };
        // output class dimension != ClassCount (3)
        yield return new object[] { new[] { 1, 3, 5 }, new[] { 1, 4 } };
    }

    [Theory]
    [MemberData(nameof(ContractViolationShapes))]
    public void ValidateModelContract_ShapeMismatch_ThrowsInvalidOperation(int[] inputDims, int[] outputDims)
    {
        Assert.Throws<InvalidOperationException>(() => PredictionService.ValidateModelContract(
            inputIsTensor: true, inputDimensions: inputDims,
            outputIsTensor: true, outputDimensions: outputDims,
            expectedWindowSize: 3, expectedFeaturesPerBar: 5, expectedClassCount: 3,
            inputNodeName: "input", outputNodeName: "output"));
    }

    [Fact]
    public void ValidateModelContract_NonTensorInput_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(() => PredictionService.ValidateModelContract(
            inputIsTensor: false, inputDimensions: new[] { 1, 3, 5 },
            outputIsTensor: true, outputDimensions: new[] { 1, 3 },
            expectedWindowSize: 3, expectedFeaturesPerBar: 5, expectedClassCount: 3,
            inputNodeName: "input", outputNodeName: "output"));
    }

    [Fact]
    public void ValidateModelContract_NonTensorOutput_ThrowsInvalidOperation()
    {
        Assert.Throws<InvalidOperationException>(() => PredictionService.ValidateModelContract(
            inputIsTensor: true, inputDimensions: new[] { 1, 3, 5 },
            outputIsTensor: false, outputDimensions: new[] { 1, 3 },
            expectedWindowSize: 3, expectedFeaturesPerBar: 5, expectedClassCount: 3,
            inputNodeName: "input", outputNodeName: "output"));
    }

    // Minimal real ONNX fixtures: input "input" [batch, 10, 5] -> Flatten -> MatMul -> Add -> Softmax
    // -> output "output" [batch, N]. Regenerate via Assets/generate_onnx_fixtures.py.
    private static readonly string ConformantModelPath = System.IO.Path.Combine("Assets", "trend_predictor_ok.onnx");
    private static readonly string BadClassCountModelPath = System.IO.Path.Combine("Assets", "trend_predictor_badclass.onnx");
    // Conformant graph + a metadata_props contract. goodmeta matches TestSettings
    // (feature_mode=ohlcv_minmax, window_size=10); badmeta declares feature_mode=zscore.
    private static readonly string GoodMetadataModelPath = System.IO.Path.Combine("Assets", "trend_predictor_goodmeta.onnx");
    private static readonly string BadMetadataModelPath = System.IO.Path.Combine("Assets", "trend_predictor_badmeta.onnx");
    // Conformant [batch,10,5] graph whose metadata declares feature_mode=zscore_joint.
    private static readonly string JointMetadataModelPath = System.IO.Path.Combine("Assets", "trend_predictor_jointmeta.onnx");
    // [batch,10,4] graph whose metadata declares feature_mode=log_return_ohlc.
    private static readonly string LogReturnOhlcModelPath = System.IO.Path.Combine("Assets", "trend_predictor_lrohlc.onnx");

    [Fact]
    public async Task PredictAsync_WithConformantRealModel_ReturnsValidPrediction()
    {
        var service = new PredictionService(new TestSettings(ConformantModelPath, predictionWindowSize: 10), new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.False(result.IsFallback);
        Assert.Equal(3, result.Scores.Count);

        float sum = 0f;
        foreach (var score in result.Scores)
        {
            Assert.InRange(score.Score, 0f, 1f);
            sum += score.Score;
        }
        Assert.Equal(1f, sum, 3);

        Assert.InRange(result.Confidence, 0f, 1f);
        Assert.Equal(result.Scores.Max(s => s.Score), result.Confidence, 5);
        Assert.True(result.Entropy >= 0f);

        var expectedLabels = new[] { "Up", "Down", "Neutral", "Unknown" };
        Assert.Contains(result.Label, expectedLabels);
    }

    [Fact]
    public async Task PredictAsync_WithNonConformantClassCount_FallsBackToEmpty()
    {
        var service = new PredictionService(new TestSettings(BadClassCountModelPath, predictionWindowSize: 10), new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.True(result.IsFallback);
        Assert.Equal("Unknown", result.Label);
        Assert.Equal(PredictionResult.Empty, result);
    }

    [Fact]
    public async Task PredictAsync_WithMatchingContractMetadata_ReturnsValidPrediction()
    {
        var service = new PredictionService(new TestSettings(GoodMetadataModelPath, predictionWindowSize: 10), new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.False(result.IsFallback);
        Assert.Equal(3, result.Scores.Count);
    }

    [Fact]
    public async Task PredictAsync_WithMismatchedFeatureModeMetadata_FallsBackToEmpty()
    {
        // TestSettings.PredictionFeatureMode is OhlcvMinMax; this model's metadata declares zscore.
        var service = new PredictionService(new TestSettings(BadMetadataModelPath, predictionWindowSize: 10), new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.True(result.IsFallback);
        Assert.Equal(PredictionResult.Empty, result);
    }

    [Fact]
    public async Task PredictAsync_WithZScoreModeAndMatchingModel_ReturnsValidPrediction()
    {
        // trend_predictor_badmeta.onnx carries feature_mode=zscore; "bad" is only relative to the
        // default OhlcvMinMax config. With a matching ZScoreStandardized config the metadata
        // cross-check passes and the per-channel Z-Score path (MLDataProcessor.NormalizeZScoreOhlcv)
        // runs end-to-end.
        var service = new PredictionService(
            new TestSettings(BadMetadataModelPath, predictionWindowSize: 10,
                predictionFeatureMode: PredictionFeatureMode.ZScoreStandardized),
            new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.False(result.IsFallback);
        Assert.Equal(3, result.Scores.Count);
    }

    [Fact]
    public async Task PredictAsync_WithNoMetadataProps_StillReturnsValidPrediction()
    {
        // trend_predictor_ok.onnx has no metadata_props: the contract cross-check must
        // warn and continue, not fail (backward compatibility with pre-contract models).
        var service = new PredictionService(new TestSettings(ConformantModelPath, predictionWindowSize: 10), new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.False(result.IsFallback);
    }

    [Fact]
    public async Task PredictAsync_WithZScoreJointModeAndMatchingModel_ReturnsValidPrediction()
    {
        // The joint Z-Score feature path (5 channels) builds the tensor via
        // MLDataProcessor.ComputeJointZScoreOhlcv and the metadata matches the config.
        var service = new PredictionService(
            new TestSettings(JointMetadataModelPath, predictionWindowSize: 10,
                predictionFeatureMode: PredictionFeatureMode.ZScoreOhlcvJoint),
            new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.False(result.IsFallback);
        Assert.Equal(3, result.Scores.Count);
    }

    [Fact]
    public async Task PredictAsync_WithZScoreJointModelButOhlcvMinMaxConfig_FallsBackToEmpty()
    {
        // Same 5-channel shape, but the model metadata (zscore_joint) disagrees with the
        // configured OhlcvMinMax: the metadata cross-check must reject it.
        var service = new PredictionService(
            new TestSettings(JointMetadataModelPath, predictionWindowSize: 10),
            new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.True(result.IsFallback);
        Assert.Equal(PredictionResult.Empty, result);
    }

    [Fact]
    public async Task PredictAsync_WithLogReturnOhlcModeAndMatchingModel_ReturnsValidPrediction()
    {
        // The 4-channel intrabar log-return path: tensor built via
        // MLDataProcessor.ComputeLogReturnsOhlc; ValidateModelContract must accept width 4.
        var service = new PredictionService(
            new TestSettings(LogReturnOhlcModelPath, predictionWindowSize: 10,
                predictionFeatureMode: PredictionFeatureMode.LogReturnOhlc),
            new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.False(result.IsFallback);
        Assert.Equal(3, result.Scores.Count);
    }

    // O-6 regression guard. Before PredictionService gated RunInference against Dispose(),
    // this interleaving reproduced a hard host crash:
    //   System.AccessViolationException: Attempted to read or write protected memory
    //     at Microsoft.ML.OnnxRuntime.InferenceSession.RunImpl(...)
    //     at StockAnalyzer.Core.Services.PredictionService.RunInference(Single[], Int32)
    // because Dispose() freed the native InferenceSession / RunOptions while an in-flight
    // RunInference was executing natively (an uncatchable CSE). The _inferenceGate lock now
    // serializes the two; this test must stay green.
    [Fact]
    public async Task PredictAsync_ConcurrentDisposeDuringInflight_NeverCrashesAndDegradesGracefully()
    {
        // Regression evidence for audit finding O-6: a Dispose() racing with in-flight
        // PredictAsync calls (which by then are inside native ONNX Runtime RunInference) must
        // degrade to a PredictionResult or a caught ObjectDisposedException. It must never
        // surface a NullReferenceException / native AccessViolation or crash the process.
        const int Iterations = 5;
        const int InFlight = 64;

        for (int iter = 0; iter < Iterations; iter++)
        {
            var service = new PredictionService(
                new TestSettings(ConformantModelPath, predictionWindowSize: 10), new MLDataProcessor());

            // Force the ONNX session to load so the racing calls reach native RunInference.
            var warmup = await service.PredictAsync(BuildCandles(20));
            Assert.False(warmup.IsFallback);

            var candles = BuildCandles(20);
            var tasks = new Task<PredictionResult>[InFlight];
            for (int i = 0; i < InFlight; i++)
            {
                tasks[i] = Task.Run(() => service.PredictAsync(candles));
            }

            // Dispose while the batch is still in flight.
            service.Dispose();

            foreach (var t in tasks)
            {
                PredictionResult result;
                try
                {
                    result = await t;
                }
                catch (ObjectDisposedException)
                {
                    // Acceptable: the disposal guard fired at or before the call entry.
                    continue;
                }

                // Reaching here means the resilience pipeline absorbed the race and produced
                // a structurally valid result (a real prediction or the Empty fallback). Any
                // other exception type escaping 'await t' fails the test.
                Assert.True(result.Confidence >= 0f && result.Confidence <= 1f);
                Assert.NotNull(result.Scores);
            }
        }
    }
}
