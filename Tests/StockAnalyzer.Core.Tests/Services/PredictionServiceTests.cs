using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;
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
        private readonly StockAnalyzer.Core.Models.Training.FeatureSpec? _predictionFeatureSpec;
        private readonly TargetType _predictionTargetType;

        public TestSettings(
            string predictionModelPath = "NonExistent/does_not_exist.onnx",
            int predictionWindowSize = 3,
            PredictionFeatureMode predictionFeatureMode = PredictionFeatureMode.OhlcvMinMax,
            StockAnalyzer.Core.Models.Training.FeatureSpec? predictionFeatureSpec = null,
            TargetType predictionTargetType = TargetType.Classification)
        {
            _predictionModelPath = predictionModelPath;
            _predictionWindowSize = predictionWindowSize;
            _predictionFeatureMode = predictionFeatureMode;
            _predictionFeatureSpec = predictionFeatureSpec;
            _predictionTargetType = predictionTargetType;
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
        public StockAnalyzer.Core.Models.Training.FeatureSpec? PredictionFeatureSpec => _predictionFeatureSpec;
        public TargetType PredictionTargetType => _predictionTargetType;
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

    // --- Composed features (Price-only, T03): ValidateComposedFeatureSpec / BuildComposedTensor ---

    private static FeatureSpec PriceOnlySpec(PriceType price, ChannelNormalization normalization = ChannelNormalization.None)
        => new() { Channels = new[] { new FeatureChannel { Kind = FeatureChannelKind.Price, Price = price, Normalization = normalization } } };

    // D03 resource bound used by these tests, matching the C#/Python-mirrored default
    // (IStockAnalyzerSettings.PredictionMaxComposedChannels / appsettings.json "Prediction"
    // section) rather than a bare literal, so the test intent reads the same as production code.
    private const int TestMaxComposedChannels = FeatureSpec.MaxChannels;

    // Real registry-backed factory (the same StockAnalyzer.Core.Models.Indicators.IndicatorFactory
    // reflection-discovered default that PredictionService itself falls back to) for tests that
    // exercise a genuinely-registered indicator type such as RSI.
    private static readonly IIndicatorFactory RealIndicatorFactory = IndicatorFactory.Default;

    // Deterministic stand-in for the "not registered" branch: real IndicatorFactory registers
    // ~160 indicator types via reflection, so there is no IndicatorType value guaranteed to stay
    // unregistered across builds; this fake makes that branch independently testable.
    private sealed class NeverRegisteredIndicatorFactory : IIndicatorFactory
    {
        public ICoreIndicator? Create(IndicatorType type, CoreIndicatorParameterBase? parameters = null) => null;
        public bool IsRegistered(IndicatorType type) => false;
        public IEnumerable<IndicatorType> GetRegisteredTypes() => Enumerable.Empty<IndicatorType>();
    }

    [Fact]
    public void ValidateComposedFeatureSpec_NullSpec_Throws()
        => Assert.Throws<InvalidOperationException>(() => PredictionService.ValidateComposedFeatureSpec(null, TestMaxComposedChannels, RealIndicatorFactory));

    [Fact]
    public void ValidateComposedFeatureSpec_UnregisteredIndicatorChannel_Throws()
    {
        var spec = new FeatureSpec
        {
            Channels = new[] { new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.RSI } },
        };
        Assert.Throws<InvalidOperationException>(
            () => PredictionService.ValidateComposedFeatureSpec(spec, TestMaxComposedChannels, new NeverRegisteredIndicatorFactory()));
    }

    [Fact]
    public void ValidateComposedFeatureSpec_IndicatorChannelWithNonDefaultParams_ReturnsSpecUnchanged()
    {
        var spec = new FeatureSpec
        {
            Channels = new[]
            {
                new FeatureChannel
                {
                    Kind = FeatureChannelKind.Indicator,
                    Indicator = IndicatorType.RSI,
                    Params = new Dictionary<string, string> { ["Period"] = "21" },
                },
            },
        };
        Assert.Same(spec, PredictionService.ValidateComposedFeatureSpec(spec, TestMaxComposedChannels, RealIndicatorFactory));
    }

    [Fact]
    public void ValidateComposedFeatureSpec_IndicatorChannelWithUnparseableParamValue_Throws()
    {
        var spec = new FeatureSpec
        {
            Channels = new[]
            {
                new FeatureChannel
                {
                    Kind = FeatureChannelKind.Indicator,
                    Indicator = IndicatorType.RSI,
                    Params = new Dictionary<string, string> { ["Period"] = "not-a-number" },
                },
            },
        };
        Assert.Throws<InvalidOperationException>(
            () => PredictionService.ValidateComposedFeatureSpec(spec, TestMaxComposedChannels, RealIndicatorFactory));
    }

    [Fact]
    public void ValidateComposedFeatureSpec_RegisteredIndicatorChannelWithDefaultParams_ReturnsSpecUnchanged()
    {
        var spec = new FeatureSpec
        {
            Channels = new[] { new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.RSI } },
        };
        Assert.Same(spec, PredictionService.ValidateComposedFeatureSpec(spec, TestMaxComposedChannels, RealIndicatorFactory));
    }

    [Theory]
    [InlineData(PriceType.HeikinAshiOpen)]
    [InlineData(PriceType.HeikinAshiHigh)]
    [InlineData(PriceType.HeikinAshiLow)]
    [InlineData(PriceType.HeikinAshiClose)]
    public void ValidateComposedFeatureSpec_HeikinAshiPriceType_Throws(PriceType priceType)
        => Assert.Throws<InvalidOperationException>(() => PredictionService.ValidateComposedFeatureSpec(PriceOnlySpec(priceType), TestMaxComposedChannels, RealIndicatorFactory));

    [Fact]
    public void ValidateComposedFeatureSpec_PriceOnlyChannels_ReturnsSpecUnchanged()
    {
        var spec = PriceOnlySpec(PriceType.Close);
        Assert.Same(spec, PredictionService.ValidateComposedFeatureSpec(spec, TestMaxComposedChannels, RealIndicatorFactory));
    }

    [Fact]
    public void ValidateComposedFeatureSpec_ChannelCountOverConfiguredBound_Throws()
    {
        var channels = new List<FeatureChannel>();
        for (int i = 0; i < 3; i++)
        {
            channels.Add(new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close });
        }
        var spec = new FeatureSpec { Channels = channels };

        var ex = Assert.Throws<InvalidOperationException>(
            () => PredictionService.ValidateComposedFeatureSpec(spec, maxChannels: 2, RealIndicatorFactory));
        Assert.Contains("MaxChannels", ex.Message);
    }

    private static List<CandleData> BuildComposedCandles()
    {
        // Mirrors StockAnalyzer.Python/training/dataset.py's _run_selfcheck "composed_features"
        // synthetic array exactly, so the two implementations can be eyeballed against each other.
        (decimal O, decimal H, decimal L, decimal C)[] rows =
        {
            (10m, 12m, 9m, 11m), (11m, 13m, 10m, 12m), (12m, 14m, 11m, 13m),
            (13m, 15m, 12m, 14m), (14m, 16m, 13m, 15m), (15m, 17m, 14m, 16m),
        };
        var candles = new List<CandleData>();
        for (int i = 0; i < rows.Length; i++)
        {
            candles.Add(new CandleData(new DateTime(2024, 1, 1).AddDays(i), rows[i].O, rows[i].H, rows[i].L, rows[i].C, 100));
        }
        return candles;
    }

    [Fact]
    public void BuildComposedTensor_CloseNoneAndHighWindowMinMax_MatchesHandComputedValues()
    {
        var spec = new FeatureSpec
        {
            Channels = new[]
            {
                new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.Close, Normalization = ChannelNormalization.None },
                new FeatureChannel { Kind = FeatureChannelKind.Price, Price = PriceType.High, Normalization = ChannelNormalization.WindowMinMax },
            },
        };
        var destination = new float[4 * 2];

        PredictionService.BuildComposedTensor(BuildComposedCandles(), startIndex: 0, windowSize: 4, spec, RealIndicatorFactory, destination);

        // channel 0 (close, none): raw closes of bars 0..3
        Assert.Equal(new[] { 11f, 12f, 13f, 14f }, new[] { destination[0], destination[2], destination[4], destination[6] });
        // channel 1 (high, window_min_max): highs [12,13,14,15] -> [0, 1/3, 2/3, 1]
        AssertFloatArrayEqual(new[] { 0f, 1f / 3f, 2f / 3f, 1f }, new[] { destination[1], destination[3], destination[5], destination[7] });
    }

    private static void AssertFloatArrayEqual(float[] expected, float[] actual, int precision = 5)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i], precision);
        }
    }

    [Fact]
    public void BuildComposedTensor_TrueHighTrueLow_ClampsAgainstPreviousBarClose()
    {
        var candles = new List<CandleData>
        {
            new(new DateTime(2024, 1, 1), 10m, 10m, 10m, 10m, 0),
            new(new DateTime(2024, 1, 2), 9m, 9.5m, 8m, 9m, 0),
            new(new DateTime(2024, 1, 3), 8m, 8.5m, 7m, 7.5m, 0),
        };
        var highDest = new float[3];
        var lowDest = new float[3];

        PredictionService.BuildComposedTensor(candles, 0, 3, PriceOnlySpec(PriceType.TrueHigh), RealIndicatorFactory, highDest);
        PredictionService.BuildComposedTensor(candles, 0, 3, PriceOnlySpec(PriceType.TrueLow), RealIndicatorFactory, lowDest);

        Assert.Equal(new[] { 10f, 10f, 9f }, highDest);
        Assert.Equal(new[] { 10f, 8f, 7f }, lowDest);
    }

    [Fact]
    public void BuildComposedTensor_IndicatorChannelSma_MatchesHandComputedValues()
    {
        // Close = 100+i for i=0..29; CoreSmaIndicator's default Period=20 makes bar i>=19's SMA the
        // mean of 20 consecutive integers ending at 100+i, i.e. 100+i-9.5 -- hand-computable without
        // depending on the indicator's own implementation to check itself.
        var candles = BuildCandles(30);
        var spec = new FeatureSpec
        {
            Channels = new[] { new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA } },
        };
        var destination = new float[5];

        PredictionService.BuildComposedTensor(candles, startIndex: 19, windowSize: 5, spec, RealIndicatorFactory, destination);

        AssertFloatArrayEqual(new[] { 109.5f, 110.5f, 111.5f, 112.5f, 113.5f }, destination);
    }

    [Fact]
    public void BuildComposedTensor_IndicatorChannelSmaWithCustomPeriod_MatchesHandComputedValues()
    {
        // Close = 100+i; SMA(10) (Period=10, overriding the registry default of 20) at bar i>=9 is
        // the mean of 10 consecutive integers ending at 100+i, i.e. 100+i-4.5 -- distinct from the
        // default-Period test's 100+i-9.5, proving the custom Params value was actually applied
        // (the same FeatureChannelConverter.BuildIndicatorSettings path IndicatorChannelExporter
        // uses on the training side, so both sides compute identical values from this channel).
        var candles = BuildCandles(30);
        var spec = new FeatureSpec
        {
            Channels = new[]
            {
                new FeatureChannel
                {
                    Kind = FeatureChannelKind.Indicator,
                    Indicator = IndicatorType.SMA,
                    Params = new Dictionary<string, string> { ["Period"] = "10" },
                },
            },
        };
        var destination = new float[5];

        PredictionService.BuildComposedTensor(candles, startIndex: 9, windowSize: 5, spec, RealIndicatorFactory, destination);

        AssertFloatArrayEqual(new[] { 104.5f, 105.5f, 106.5f, 107.5f, 108.5f }, destination);
    }

    [Fact]
    public void BuildComposedTensor_IndicatorChannelWarmupBar_ThrowsInvalidOperationException()
    {
        // Window [15,20) includes bar 18, still inside SMA(20)'s warmup (first valid bar is index
        // 19); inference must reject an unwarmed indicator bar rather than silently zero-filling it.
        var candles = BuildCandles(30);
        var spec = new FeatureSpec
        {
            Channels = new[] { new FeatureChannel { Kind = FeatureChannelKind.Indicator, Indicator = IndicatorType.SMA } },
        };
        var destination = new float[5];

        var ex = Assert.Throws<InvalidOperationException>(
            () => PredictionService.BuildComposedTensor(candles, startIndex: 15, windowSize: 5, spec, RealIndicatorFactory, destination));
        Assert.Contains("warmup", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildComposedTensor_WindowZScore_IsPopulationZScorePerChannel()
    {
        var candles = BuildComposedCandles();
        var destination = new float[4];

        PredictionService.BuildComposedTensor(
            candles, 0, 4, PriceOnlySpec(PriceType.Close, ChannelNormalization.WindowZScore), RealIndicatorFactory, destination);

        double[] closes = { 11.0, 12.0, 13.0, 14.0 };
        double mu = closes.Average();
        double sigma = Math.Sqrt(closes.Select(c => (c - mu) * (c - mu)).Average());
        var expected = closes.Select(c => (float)((c - mu) / sigma)).ToArray();
        AssertFloatArrayEqual(expected, destination);
    }

    // --- D03 resource bound: ComputeCheckedFeatureCount (MaxTensorSizeMB, caller-supplied) ---

    // The bound these tests pass in mirrors the configured default
    // (IStockAnalyzerSettings.PredictionMaxComposedTensorSizeMB = 256 MB), supplied explicitly
    // here as production code now does via _settings rather than a compiled constant.
    private const long TestMaxTensorBytes = 256L * 1024 * 1024;

    [Fact]
    public void ComputeCheckedFeatureCount_UnderBound_ReturnsProduct()
    {
        int featureCount = PredictionService.ComputeCheckedFeatureCount(windowSize: 60, featuresPerBar: 5, TestMaxTensorBytes);
        Assert.Equal(300, featureCount);
    }

    [Fact]
    public void ComputeCheckedFeatureCount_AtByteBound_ReturnsProduct()
    {
        // 256 MB / sizeof(float) = 67,108,864 elements exactly at the bound.
        const int windowSize = 65536;
        const int featuresPerBar = 1024;
        int featureCount = PredictionService.ComputeCheckedFeatureCount(windowSize, featuresPerBar, TestMaxTensorBytes);
        Assert.Equal(windowSize * featuresPerBar, featureCount);
    }

    [Fact]
    public void ComputeCheckedFeatureCount_OverByteBound_Throws()
    {
        const int windowSize = 65536;
        const int featuresPerBar = 1025;
        var ex = Assert.Throws<InvalidOperationException>(
            () => PredictionService.ComputeCheckedFeatureCount(windowSize, featuresPerBar, TestMaxTensorBytes));
        Assert.Contains("MaxTensorSizeMB", ex.Message);
    }

    [Fact]
    public void ComputeCheckedFeatureCount_IntOverflowingProduct_ThrowsInsteadOfWrapping()
    {
        // windowSize * featuresPerBar overflows a 32-bit int (> 2^31), which the bound check
        // must reject via checked long arithmetic rather than silently wrapping to a small or
        // negative value.
        var ex = Assert.Throws<InvalidOperationException>(
            () => PredictionService.ComputeCheckedFeatureCount(windowSize: 100_000, featuresPerBar: 100_000, TestMaxTensorBytes));
        Assert.Contains("MaxTensorSizeMB", ex.Message);
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
    public void ValidateModelContract_RegressionWidthOne_DoesNotThrow()
    {
        // A regression head's expected output width is 1, not a class count.
        PredictionService.ValidateModelContract(
            inputIsTensor: true, inputDimensions: new[] { 1, 10, 5 },
            outputIsTensor: true, outputDimensions: new[] { 1, 1 },
            expectedWindowSize: 10, expectedFeaturesPerBar: 5, expectedClassCount: 1,
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
    // [batch,10,5] -> [batch,1] regression graph (zero weight, constant bias == REGRESSION_FIXTURE_Y
    // in generate_onnx_fixtures.py) + metadata_props target_type=regression.
    private static readonly string RegressionModelPath = System.IO.Path.Combine("Assets", "trend_predictor_regression.onnx");
    private const float RegressionFixtureY = 0.04f;

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

    [Fact]
    public async Task PredictAsync_WithRegressionModel_ReturnsRegressionResult()
    {
        var service = new PredictionService(
            new TestSettings(RegressionModelPath, predictionWindowSize: 10,
                predictionTargetType: TargetType.Regression),
            new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.False(result.IsFallback);
        Assert.True(result.IsRegression);
        Assert.Equal(RegressionFixtureY, result.PredictedLogReturn, 5);
        Assert.Equal(100.0 * (Math.Exp(RegressionFixtureY) - 1.0), result.SimpleReturnPercent, 5);

        // Classification-only fields carry no fabricated value for a regression result.
        Assert.Equal(string.Empty, result.Label);
        Assert.Equal(0f, result.Confidence);
        Assert.Equal(0f, result.Entropy);
        Assert.Empty(result.Scores);
    }

    [Fact]
    public async Task PredictAsync_WithClassificationModelButRegressionConfig_FallsBackToEmpty()
    {
        // trend_predictor_ok.onnx outputs width 3 (a 3-class head); configuring
        // PredictionTargetType.Regression expects width 1, so the load-time contract must reject it.
        var service = new PredictionService(
            new TestSettings(ConformantModelPath, predictionWindowSize: 10,
                predictionTargetType: TargetType.Regression),
            new MLDataProcessor());

        var result = await service.PredictAsync(BuildCandles(20));

        Assert.True(result.IsFallback);
        Assert.Equal(PredictionResult.Empty, result);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(0.04f)]
    [InlineData(-0.1f)]
    public void ComputeSimpleReturnPercent_KnownY_MatchesFormula(float y)
    {
        double expected = 100.0 * (Math.Exp((double)y) - 1.0);

        Assert.Equal(expected, PredictionService.ComputeSimpleReturnPercent(y), 10);
    }

    [Fact]
    public void ExtractRegressionValue_SingleFiniteValue_ReturnsIt()
    {
        Assert.Equal(0.04f, PredictionService.ExtractRegressionValue(new float[] { 0.04f }));
    }

    [Theory]
    [InlineData(new float[] { })]
    [InlineData(new float[] { 1f, 2f })]
    public void ExtractRegressionValue_WrongLength_Throws(float[] output)
    {
        Assert.Throws<InvalidOperationException>(() => PredictionService.ExtractRegressionValue(output));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ExtractRegressionValue_NonFinite_Throws(float y)
    {
        Assert.Throws<InvalidOperationException>(() => PredictionService.ExtractRegressionValue(new[] { y }));
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
