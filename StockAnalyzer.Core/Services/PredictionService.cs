using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Fallback;
using Polly.Retry;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Implementation of <see cref="IPredictionService"/> using ONNX Runtime.
/// </summary>
public class PredictionService : IPredictionService, IDisposable
{
    public string? ActiveModelId => _modelRegistry?.ActiveId;

    private volatile bool _disposed;
    private readonly IStockAnalyzerSettings _settings;
    private readonly IMLDataProcessor _dataProcessor;
    private readonly ILogger<PredictionService> _logger;
    private readonly ResiliencePipeline<PredictionResult> _resiliencePipeline;
    private readonly System.Threading.SemaphoreSlim _initLock = new(1, 1);
    // Owns runtime replacement, in-flight references and native RunOptions lifetime.
    private readonly object _inferenceGate = new();
    // Microsoft.ML.OnnxRuntime 1.24.3's Run(runOptions, ...) overload dereferences runOptions
    // in native interop, so a null throws NullReferenceException; reuse one default instance.
    private readonly RunOptions _runOptions = new();
    private const int FeaturesPerCandle = 5;

    internal static int ResolveFeaturesPerBar(PredictionFeatureMode mode, FeatureSpec? spec) => mode switch
    {
        PredictionFeatureMode.LogReturn => 1,
        PredictionFeatureMode.LogReturnOhlc => 4,
        PredictionFeatureMode.ComposedFeatures => spec?.Channels.Count ?? 0,
        _ => FeaturesPerCandle,
    };

    private readonly IModelGenerationRegistry? _modelRegistry;
    private volatile RuntimeBundle? _runtime;
    private string? _failedModelId;
    private string? _resolvedModelPath;
    private volatile bool _permanentInitializationFailure;
    private readonly IIndicatorFactory _indicatorFactory;

    private sealed class RuntimeBundle : IDisposable
    {
        internal RuntimeBundle(InferenceSession session, ModelGenerationLease? lease, FixedScaler? scaler, string? modelId,
            string inputName, string outputName, int window, int featuresPerBar,
            PredictionFeatureMode mode, FeatureSpec? featureSpec, TargetType target,
            PredictionOutputContract? outputContract,
            string[] classLabels, long maxTensorBytes)
        {
            Session = session;
            Lease = lease;
            Scaler = scaler;
            ModelId = modelId;
            InputNames = new[] { inputName };
            OutputNames = new[] { outputName };
            InputShape = new long[] { 1, window, featuresPerBar };
            Window = window;
            FeaturesPerBar = featuresPerBar;
            Mode = mode;
            FeatureSpec = featureSpec;
            Target = target;
            OutputContract = outputContract;
            ClassLabels = classLabels;
            MaxTensorBytes = maxTensorBytes;
        }

        internal InferenceSession Session { get; }
        internal ModelGenerationLease? Lease { get; }
        internal FixedScaler? Scaler { get; }
        internal string? ModelId { get; }
        internal string[] InputNames { get; }
        internal string[] OutputNames { get; }
        internal long[] InputShape { get; }
        internal int Window { get; }
        internal int FeaturesPerBar { get; }
        internal PredictionFeatureMode Mode { get; }
        internal FeatureSpec? FeatureSpec { get; }
        internal TargetType Target { get; }
        internal PredictionOutputContract? OutputContract { get; }
        internal string[] ClassLabels { get; }
        internal long MaxTensorBytes { get; }
        // Both fields are protected by _inferenceGate.
        internal int Users { get; set; }
        internal bool Retired { get; set; }

        public void Dispose()
        {
            try { Session.Dispose(); }
            finally { Lease?.Dispose(); }
        }
    }

    private static FeatureSpec CopyFeatureSpec(FeatureSpec spec) => spec with
    {
        Channels = Array.AsReadOnly(spec.Channels.Select(channel => channel with
        {
            Params = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
                channel.Params.ToDictionary(entry => entry.Key, entry => entry.Value)),
        }).ToArray()),
        Lags = Array.AsReadOnly((spec.Lags ?? Array.Empty<int>()).ToArray()),
    };

    internal static CandleData[] SnapshotHistory(IEnumerable<CandleData> candles)
    {
        if (candles is not IReadOnlyList<CandleData> list) return candles.ToArray();
        var snapshot = new CandleData[list.Count];
        for (int i = 0; i < snapshot.Length; i++) snapshot[i] = list[i];
        return snapshot;
    }

    internal static void ValidateChronologicalHistory(IReadOnlyList<CandleData> history)
    {
        for (int i = 1; i < history.Count; i++)
            if (history[i].Timestamp <= history[i - 1].Timestamp)
                throw new InvalidDataException("Candle timestamps must be strictly increasing.");
    }

    private RuntimeBundle? AcquireRuntime()
    {
        lock (_inferenceGate)
        {
            if (_disposed || _runtime is null || _runtime.ModelId != _modelRegistry?.ActiveId)
                return null;
            _runtime.Users++;
            return _runtime;
        }
    }

    private void ReleaseRuntime(RuntimeBundle runtime)
    {
        lock (_inferenceGate)
        {
            runtime.Users--;
            if (runtime.Users == 0 && runtime.Retired) runtime.Dispose();
        }
    }

    private static void Retire(RuntimeBundle? runtime)
    {
        if (runtime is null) return;
        runtime.Retired = true;
        if (runtime.Users == 0) runtime.Dispose();
    }

    public PredictionService(
        IStockAnalyzerSettings settings,
        IMLDataProcessor dataProcessor,
        ILogger<PredictionService>? logger = null,
        IIndicatorFactory? indicatorFactory = null,
        IModelGenerationRegistry? modelRegistry = null)
    {
        _settings = settings;
        _dataProcessor = dataProcessor;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PredictionService>.Instance;
        // Only exercised by PredictionFeatureMode.ComposedFeatures Indicator-kind channels; every
        // other caller/test never touches this, so it defaults to the same reflection-discovered
        // registry instance IndicatorFactory itself already exposes for non-DI callers, instead of
        // forcing every existing call site to thread one through.
        _indicatorFactory = indicatorFactory ?? IndicatorFactory.Default;
        _modelRegistry = modelRegistry;
        _resiliencePipeline = BuildResiliencePipeline();
    }

    private ResiliencePipeline<PredictionResult> BuildResiliencePipeline()
    {
        return new ResiliencePipelineBuilder<PredictionResult>()
            .AddFallback(new FallbackStrategyOptions<PredictionResult>
            {
                ShouldHandle = new PredicateBuilder<PredictionResult>()
                    .Handle<Exception>(),
                FallbackAction = _ => Outcome.FromResultAsValueTask(PredictionResult.Empty),
                OnFallback = args =>
                {
                    _logger.LogWarning(args.Outcome.Exception, "Prediction fallback triggered; returning Empty result.");
                    return default;
                },
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<PredictionResult>
            {
                FailureRatio = _settings.CircuitBreakerFailureRatio,
                MinimumThroughput = _settings.CircuitBreakerMinimumThroughput,
                BreakDuration = TimeSpan.FromMilliseconds(_settings.CircuitBreakerBreakDurationMs),
                SamplingDuration = TimeSpan.FromMilliseconds(_settings.CircuitBreakerSamplingDurationMs),
                ShouldHandle = new PredicateBuilder<PredictionResult>()
                    .Handle<Exception>(),
                OnOpened = args =>
                {
                    _logger.LogError("Prediction circuit breaker opened after repeated failures.");
                    return default;
                },
            })
            .AddRetry(new RetryStrategyOptions<PredictionResult>
            {
                MaxRetryAttempts = _settings.PredictionRetryMaxAttempts,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromMilliseconds(_settings.PredictionRetryBaseDelayMs),
                MaxDelay = TimeSpan.FromMilliseconds(_settings.PredictionRetryMaxDelayMs),
                ShouldHandle = new PredicateBuilder<PredictionResult>()
                    .Handle<Exception>(),
                OnRetry = args =>
                {
                    _logger.LogWarning("Prediction attempt {Attempt} failed: {Error}. Retrying...", args.AttemptNumber + 1, args.Outcome.Exception?.Message);
                    return default;
                },
            })
            .Build();
    }

    public async Task InitializeAsync()
    {
        var activeId = _modelRegistry?.ActiveId;
        if ((_runtime != null && _runtime.ModelId == activeId)
            || (_permanentInitializationFailure && _failedModelId == activeId)) return;

        // _initLock is never disposed (see Dispose), so neither this wait nor the Release below can throw ObjectDisposedException; a call that
        // queued before Dispose acquires the lock in turn, sees _disposed and returns.
        await _initLock.WaitAsync();

        try
        {
            activeId = _modelRegistry?.ActiveId;
            if (_disposed || (_runtime != null && _runtime.ModelId == activeId)
                || (_permanentInitializationFailure && _failedModelId == activeId)) return;
            // Load model asynchronously to prevent UI thread blocking
            await Task.Run(() => EnsureModelLoaded());
            _logger.LogInformation("ONNX model loaded successfully from {Path}.", _resolvedModelPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ONNX model from {Path}.", _resolvedModelPath ?? _settings.PredictionModelPath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void EnsureModelLoaded()
    {
        var lease = _modelRegistry?.AcquireActive();
        var candidateId = lease?.ModelId;
        var modelPath = lease?.ModelPath ?? Common.PathDiscovery.ResolvePredictionModelPath(_settings.PredictionModelPath);
        _resolvedModelPath = modelPath;
        if (!System.IO.File.Exists(modelPath))
        {
            // The model file's absence cannot self-heal without an app restart or config change,
            // so further InitializeAsync calls skip the redundant File.Exists + InferenceSession attempt.
            _permanentInitializationFailure = true;
            _failedModelId = candidateId;
            lease?.Dispose();
            throw new System.IO.FileNotFoundException("ONNX model file not found.", modelPath);
        }

        // ONNX Runtime copies the options into the session at construction, so the
        // SessionOptions native handle can be released as soon as this method returns.
        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        InferenceSession session;
        try { session = new InferenceSession(modelPath, sessionOptions); }
        catch
        {
            lease?.Dispose();
            _permanentInitializationFailure = true;
            _failedModelId = candidateId;
            throw;
        }
        try
        {
            var inputName = ResolveNodeName(session.InputMetadata.Keys, _settings.PredictionInputNodeName);
            var outputName = ResolveNodeName(session.OutputMetadata.Keys, _settings.PredictionOutputNodeName);
            var mode = _settings.PredictionFeatureMode;
            var window = _settings.PredictionWindowSize;
            var classLabels = _settings.PredictionClassLabels.ToArray();
            var maxTensorBytes = (long)_settings.PredictionMaxComposedTensorSizeMB * 1024 * 1024;

            if (!session.InputMetadata.TryGetValue(inputName, out var inputMeta))
            {
                throw new InvalidOperationException($"Configured ONNX input node '{inputName}' is not present in the model.");
            }

            if (!session.OutputMetadata.TryGetValue(outputName, out var outputMeta))
            {
                throw new InvalidOperationException($"Configured ONNX output node '{outputName}' is not present in the model.");
            }

            // Composed features have no fixed channel set: the spec must be configured and every
            // channel either a non-Heikin-Ashi Price series or a registered Indicator with
            // registry-default Params (Heikin-Ashi price types and non-default indicator Params
            // are a follow-up task, rejected explicitly here rather than silently mis-composed)
            // before anything else about this model can be validated.
            FeatureSpec? featureSpec = null;
            string? expectedFeatureSpecJson = null;
            if (mode == PredictionFeatureMode.ComposedFeatures)
            {
                featureSpec = CopyFeatureSpec(ValidateComposedFeatureSpec(
                    _settings.PredictionFeatureSpec, _settings.PredictionMaxComposedChannels, _indicatorFactory));
                expectedFeatureSpecJson = System.Text.Json.JsonSerializer.Serialize(
                    featureSpec, TrainingConfigJson.Options);
            }

            var metadata = session.ModelMetadata.CustomMetadataMap;
            var outputContract = PredictionModelMetadata.ReadOutputContract(metadata);
            var target = outputContract.Semantic switch
            {
                PredictionOutputSemantic.LogReturn => TargetType.Regression,
                PredictionOutputSemantic.ClassProbabilities => TargetType.Classification,
                _ => throw new InvalidOperationException("ONNX output semantic is unsupported."),
            };
            var fixedScaler = PredictionModelMetadata.RequiresFixedScaler(metadata)
                ? FixedScaler.Load(modelPath, metadata) : null;
            int featuresPerBar = fixedScaler?.Statistics.Length
                ?? ResolveFeaturesPerBar(mode, featureSpec);
            // A regression head outputs a single continuous value [batch, 1], not a
            // per-class probability vector, so its expected output width is 1 regardless
            // of the configured PredictionClassLabels (which are meaningless for it).
            int expectedOutputWidth = target == TargetType.Regression
                ? 1
                : classLabels.Length;
            ValidateModelContract(inputMeta, outputMeta, window, featuresPerBar,
                expectedOutputWidth, inputName, outputName);

            // Semantic contract: the model's embedded metadata_props (feature_mode / window_size /
            // class_order) must match the running configuration. A mismatch here is as
            // unrecoverable as a shape mismatch, so the surrounding catch treats it identically.
            PredictionModelMetadata.Validate(
                metadata,
                mode,
                window,
                classLabels,
                _logger,
                expectedFeatureSpecJson);

            var loaded = new RuntimeBundle(session, lease, fixedScaler, candidateId, inputName, outputName,
                window, featuresPerBar, mode, featureSpec, target, outputContract, classLabels, maxTensorBytes);

            lock (_inferenceGate)
            {
                if (_disposed || _modelRegistry?.ActiveId != candidateId)
                {
                    // Disposed while this load was in flight: drop the session instead of
                    // publishing a native handle that Dispose() has already stopped tracking.
                    loaded.Dispose();
                    return;
                }

                var previous = _runtime;
                _runtime = loaded;
                Retire(previous);
                _permanentInitializationFailure = false;
                _failedModelId = null;
            }
        }
        catch
        {
            // A shape/contract mismatch cannot self-heal without a model or config change, so mirror
            // the FileNotFound path: dispose the session and stop retrying on every PredictAsync call.
            session.Dispose();
            lease?.Dispose();
            _permanentInitializationFailure = true;
            _failedModelId = candidateId;
            throw;
        }
    }

    /// <summary>
    /// Validates the loaded ONNX model against the configured inference contract:
    /// input tensor rank 3 <c>[batch, WindowSize, FeaturesPerBar]</c> and output tensor rank 2
    /// <c>[batch, ClassCount]</c>. Batch is 1 or dynamic (-1); remaining dynamic dimensions
    /// retain the existing behavior. Node metadata must declare float32 input and output.
    /// Every violation throws <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static void ValidateModelContract(NodeMetadata inputMetadata, NodeMetadata outputMetadata,
        int expectedWindowSize, int expectedFeaturesPerBar, int expectedClassCount,
        string inputNodeName, string outputNodeName)
    {
        if (inputMetadata.ElementType != typeof(float) || outputMetadata.ElementType != typeof(float))
            throw new InvalidOperationException("ONNX model input and output must be float32 tensors.");
        ValidateModelContract(inputMetadata.IsTensor, inputMetadata.Dimensions,
            outputMetadata.IsTensor, outputMetadata.Dimensions,
            expectedWindowSize, expectedFeaturesPerBar, expectedClassCount, inputNodeName, outputNodeName);
    }

    internal static void ValidateModelContract(
        bool inputIsTensor, int[]? inputDimensions,
        bool outputIsTensor, int[]? outputDimensions,
        int expectedWindowSize, int expectedFeaturesPerBar, int expectedClassCount,
        string inputNodeName, string outputNodeName)
    {
        if (!inputIsTensor)
        {
            throw new InvalidOperationException($"ONNX input node '{inputNodeName}' is not a tensor.");
        }

        if (!outputIsTensor)
        {
            throw new InvalidOperationException($"ONNX output node '{outputNodeName}' is not a tensor.");
        }

        const int ExpectedInputRank = 3;
        const int ExpectedOutputRank = 2;

        if (inputDimensions is { Length: > 0 })
        {
            if (inputDimensions.Length != ExpectedInputRank)
            {
                throw new InvalidOperationException(
                    $"ONNX input node '{inputNodeName}' has rank {inputDimensions.Length}; expected {ExpectedInputRank} ([batch, {expectedWindowSize}, {expectedFeaturesPerBar}]).");
            }

            if (inputDimensions[0] is not (1 or -1))
                throw new InvalidOperationException($"ONNX input node '{inputNodeName}' must have batch dimension 1 or dynamic.");

            if (inputDimensions[1] > 0 && inputDimensions[1] != expectedWindowSize)
            {
                throw new InvalidOperationException(
                    $"ONNX input node '{inputNodeName}' sequence dimension {inputDimensions[1]} != configured WindowSize {expectedWindowSize}.");
            }

            if (inputDimensions[2] > 0 && inputDimensions[2] != expectedFeaturesPerBar)
            {
                throw new InvalidOperationException(
                    $"ONNX input node '{inputNodeName}' feature dimension {inputDimensions[2]} != expected {expectedFeaturesPerBar} for the configured FeatureMode.");
            }
        }

        if (outputDimensions is { Length: > 0 })
        {
            if (outputDimensions.Length != ExpectedOutputRank)
            {
                throw new InvalidOperationException(
                    $"ONNX output node '{outputNodeName}' has rank {outputDimensions.Length}; expected {ExpectedOutputRank} ([batch, {expectedClassCount}]).");
            }

            if (outputDimensions[0] is not (1 or -1))
                throw new InvalidOperationException($"ONNX output node '{outputNodeName}' must have batch dimension 1 or dynamic.");

            if (outputDimensions[1] > 0 && outputDimensions[1] != expectedClassCount)
            {
                throw new InvalidOperationException(
                    $"ONNX output node '{outputNodeName}' class dimension {outputDimensions[1]} != configured ClassLabels count {expectedClassCount}.");
            }
        }
    }

    /// <summary>
    /// Deterministically resolves an ONNX node name: uses the configured name if provided,
    /// the sole node if only one exists, or the ordinal-sorted first key when multiple nodes exist.
    /// </summary>
    internal static string ResolveNodeName(IEnumerable<string> availableNames, string? configuredName)
    {
        if (!string.IsNullOrWhiteSpace(configuredName)) return configuredName;
        var names = availableNames as IReadOnlyList<string> ?? availableNames.ToList();
        if (names.Count == 1) return names[0];
        return names.OrderBy(k => k, StringComparer.Ordinal).First();
    }

    /// <summary>
    /// D03 Overflow rule: validates the <c>windowSize * featuresPerBar</c> product with checked,
    /// wide (<see langword="long"/>) arithmetic and rejects it, before any buffer is rented, if
    /// its byte size (assuming <see langword="float"/> elements) exceeds <paramref name="maxTensorBytes"/>
    /// (the D03 resource bound, sourced from configuration by the caller -- see
    /// <c>IStockAnalyzerSettings.PredictionMaxComposedTensorSizeMB</c> -- rather than compiled
    /// here). Returns the product narrowed to <see langword="int"/> (always safe once the
    /// byte-size check has passed against any realistic bound) for use as the ONNX input
    /// tensor's element count.
    /// </summary>
    internal static int ComputeCheckedFeatureCount(int windowSize, int featuresPerBar, long maxTensorBytes)
    {
        long tensorElements = checked((long)windowSize * featuresPerBar);
        long tensorBytes = checked(tensorElements * sizeof(float));
        if (tensorBytes > maxTensorBytes)
        {
            throw new InvalidOperationException(
                $"Requested input tensor ({windowSize} bars x {featuresPerBar} channels = " +
                $"{tensorBytes:N0} bytes) exceeds the MaxTensorSizeMB bound of {maxTensorBytes / (1024 * 1024)} MB.");
        }

        return checked((int)tensorElements);
    }

    /// <summary>
    /// Guards a <see cref="PredictionFeatureMode.ComposedFeatures"/> configuration before any
    /// tensor is built from it: <paramref name="spec"/> must be configured, its channel count
    /// must not exceed <paramref name="maxChannels"/> (the D03 resource bound, sourced from
    /// configuration by the caller), and every channel must be either a <see cref="FeatureChannelKind.Price"/>
    /// channel with a non-Heikin-Ashi <see cref="PriceType"/> (Heikin-Ashi price types need
    /// recursive state across the full candle history that this release does not thread through
    /// inference) or a <see cref="FeatureChannelKind.Indicator"/> channel registered in
    /// <paramref name="indicatorFactory"/>. Every <see cref="FeatureChannel.Params"/> (including empty)
    /// is validated via <see cref="FeatureChannelConverter.ResolveParameterObject"/> (an unparseable
    /// value throws there; an unrecognized key is warned-and-skipped per that method's own
    /// contract, same as the Training Wizard's parameter picker). Returns <paramref name="spec"/>
    /// unchanged for convenient call-site chaining.
    /// </summary>
    internal static FeatureSpec ValidateComposedFeatureSpec(FeatureSpec? spec, int maxChannels, IIndicatorFactory indicatorFactory)
    {
        if (spec is null)
        {
            throw new InvalidOperationException(
                "PredictionFeatureMode is ComposedFeatures but no PredictionFeatureSpec is configured.");
        }

        if (!spec.IsValid(out var specError, maxChannels))
        {
            throw new InvalidOperationException($"PredictionFeatureSpec is invalid. {specError}");
        }

        foreach (var channel in spec.Channels)
        {
            switch (channel.Kind)
            {
                case FeatureChannelKind.Price:
                    if (channel.Price is null)
                    {
                        throw new InvalidOperationException("Composed features: a 'Price' kind channel requires a Price value.");
                    }

                    if (IsHeikinAshi(channel.Price.Value))
                    {
                        throw new InvalidOperationException(
                            $"Composed features: price type '{channel.Price.Value}' is a Heikin-Ashi variant, " +
                            "which is not yet supported (it needs recursive state across the full candle " +
                            "history that this release does not thread through inference).");
                    }

                    break;

                case FeatureChannelKind.Indicator:
                    if (channel.Indicator is null || !indicatorFactory.IsRegistered(channel.Indicator.Value))
                    {
                        throw new InvalidOperationException(
                            $"Composed features: indicator channel type '{channel.Indicator}' is not registered in IIndicatorFactory.");
                    }

                    // Validates that Params can actually be applied (an unparseable value throws
                    // here via FeatureChannelConverter.ApplyParams); the resulting parameter object
                    // is discarded and rebuilt per-call by BuildComposedTensor, matching
                    // FeatureChannelPickerViewModel.cs's own `out _` discard for the same call.
                    FeatureChannelConverter.ResolveParameterObject(channel, indicatorFactory, out _);

                    break;

                default:
                    throw new InvalidOperationException($"Composed features: channel kind '{channel.Kind}' is not supported.");
            }
        }

        return spec;
    }

    private static bool IsHeikinAshi(PriceType priceType) => priceType is
        PriceType.HeikinAshiOpen or PriceType.HeikinAshiHigh or PriceType.HeikinAshiLow or PriceType.HeikinAshiClose;

    /// <summary>
    /// Builds the composed-features input tensor for one inference window: for each channel in
    /// <paramref name="spec"/> (already validated by <see cref="ValidateComposedFeatureSpec"/>), a
    /// <see cref="FeatureChannelKind.Price"/> channel selects its series via
    /// <see cref="PriceDataHelper.ExtractPrice"/> (the same SSoT the Training Wizard's exporter and
    /// <c>dataset.compose_price_channel_window</c> mirror) and a <see cref="FeatureChannelKind.Indicator"/>
    /// channel is computed once over the full <paramref name="candles"/> history via
    /// <paramref name="indicatorFactory"/> and <see cref="IIndicatorResult.MainValues"/> (the same
    /// SSoT <see cref="Services.IndicatorChannelExporter"/> uses for the training side, so both
    /// paths compute identical values from one formula source); either kind then has its configured
    /// <see cref="ChannelNormalization"/> applied, writing bar-major / channel-minor into
    /// <paramref name="destination"/> (<c>destination[bar * channelCount + channel]</c>), matching
    /// every other feature mode's existing tensor layout in this class.
    /// </summary>
    internal static void BuildComposedTensor(
        IReadOnlyList<CandleData> candles, int startIndex, int windowSize, FeatureSpec spec, IIndicatorFactory indicatorFactory, Span<float> destination)
    {
        int channelCount = spec.Channels.Count;
        if (destination.Length < windowSize * channelCount)
        {
            throw new ArgumentException("destination span is too small for the composed tensor.", nameof(destination));
        }

        // Lazily built and shared across every Indicator-kind channel in this spec: this
        // conversion (and the resulting Calculate() cost) is skipped entirely for a Price-only spec.
        IReadOnlyList<CoreCandleData>? coreCandles = null;

        Span<double> raw = new double[windowSize];
        for (int ch = 0; ch < channelCount; ch++)
        {
            var channel = spec.Channels[ch];
            if (channel.Kind == FeatureChannelKind.Price)
            {
                var priceType = channel.Price!.Value;
                for (int i = 0; i < windowSize; i++)
                {
                    int idx = startIndex + i;
                    decimal? prevClose = idx > 0 ? candles[idx - 1].Close : (decimal?)null;
                    raw[i] = (double)PriceDataHelper.ExtractPrice(candles[idx], priceType, prevClose);
                }
            }
            else
            {
                coreCandles ??= candles
                    .Select(c => new CoreCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume))
                    .ToList();

                var indicatorType = channel.Indicator!.Value;
                var parameterObject = FeatureChannelConverter.ResolveParameterObject(channel, indicatorFactory, out _);
                var indicator = indicatorFactory.Create(indicatorType, parameterObject)
                    ?? throw new InvalidOperationException(
                        $"Composed features: IIndicatorFactory.Create returned null for registered IndicatorType '{indicatorType}'.");

                var calcResult = indicator.Calculate(coreCandles);
                if (!calcResult.IsSuccessful)
                {
                    throw new InvalidOperationException(
                        $"Composed features: indicator '{indicatorType}' calculation failed: {calcResult.ErrorMessage}");
                }

                if (calcResult.MainValues.Count != coreCandles.Count)
                {
                    throw new InvalidOperationException(
                        $"Composed features: indicator '{indicatorType}' returned {calcResult.MainValues.Count} values " +
                        $"for {coreCandles.Count} candles (expected one value per candle).");
                }

                for (int i = 0; i < windowSize; i++)
                {
                    int idx = startIndex + i;
                    var value = calcResult.MainValues[idx];
                    if (!value.HasValue)
                    {
                        throw new InvalidOperationException(
                            $"Composed features: indicator channel [{ch}] ('{indicatorType}') has no value at bar index {idx} " +
                            "(warmup period); inference cannot use an unwarmed indicator bar.");
                    }

                    raw[i] = (double)value.Value;
                }
            }

            ApplyChannelNormalization(raw, channel.Normalization, destination, ch, channelCount);
        }
    }

    /// <summary>Builds causal raw channel groups in base, then configured lag order.</summary>
    internal static void BuildFixedRawTensor(IReadOnlyList<CandleData> candles, int startIndex,
        FixedScaler scaler, FeatureSpec? spec, IIndicatorFactory indicatorFactory, Span<double> destination)
    {
        int baseWidth = spec?.Channels.Count ?? FeaturesPerCandle;
        int width = checked(baseWidth * (1 + scaler.Lags.Length));
        if (startIndex < scaler.Lags.DefaultIfEmpty(0).Max() || startIndex + scaler.WindowSize > candles.Count
            || destination.Length != checked(scaler.WindowSize * width)
            || width != scaler.Statistics.Length)
            throw new InvalidOperationException("Insufficient lag history or inconsistent fixed feature shape.");

        IReadOnlyList<CoreCandleData>? coreCandles = null;
        var indicatorValues = new double[baseWidth][];
        for (int group = 0; group <= scaler.Lags.Length; group++)
        {
            int lag = group == 0 ? 0 : scaler.Lags[group - 1];
            for (int ch = 0; ch < baseWidth; ch++)
            {
                var channel = spec?.Channels[ch];
                if (channel is { Kind: FeatureChannelKind.Indicator })
                {
                    var values = indicatorValues[ch];
                    if (values is null)
                    {
                        coreCandles ??= candles.Select(c => new CoreCandleData(
                            c.Timestamp, c.Open, c.High, c.Low, c.Close, c.Volume)).ToList();
                        var indicatorType = channel.Indicator!.Value;
                        var parameters = FeatureChannelConverter.ResolveParameterObject(channel, indicatorFactory, out _);
                        var indicator = indicatorFactory.Create(indicatorType, parameters)
                            ?? throw new InvalidOperationException("Fixed feature indicator could not be created.");
                        var result = indicator.Calculate(coreCandles);
                        if (!result.IsSuccessful || result.MainValues.Count != candles.Count)
                            throw new InvalidOperationException("Fixed feature indicator calculation failed.");
                        values = result.MainValues.Select(value => value.HasValue ? (double)value.Value : double.NaN).ToArray();
                        indicatorValues[ch] = values;
                    }
                    for (int row = 0; row < scaler.WindowSize; row++)
                    {
                        int source = startIndex + row - lag;
                        var value = values[source];
                        if (!double.IsFinite(value))
                            throw new InvalidOperationException("Fixed feature indicator is in its warmup period.");
                        destination[row * width + group * baseWidth + ch] = value;
                    }
                    continue;
                }
                for (int row = 0; row < scaler.WindowSize; row++)
                {
                    int source = startIndex + row - lag;
                    var candle = candles[source];
                    if (channel is { Kind: FeatureChannelKind.Price })
                    {
                        decimal? previous = source > 0 ? candles[source - 1].Close : null;
                        destination[row * width + group * baseWidth + ch] =
                            (double)PriceDataHelper.ExtractPrice(candle, channel.Price!.Value, previous);
                    }
                    else
                    {
                        destination[row * width + group * baseWidth + ch] = ch switch
                        {
                            0 => (double)candle.Open, 1 => (double)candle.High,
                            2 => (double)candle.Low, 3 => (double)candle.Close,
                            _ => (double)candle.Volume,
                        };
                    }
                }
            }
        }
    }

    /// <summary>
    /// Per-channel window normalization, mirroring <c>dataset._apply_channel_normalization</c>:
    /// <see cref="ChannelNormalization.WindowMinMax"/> is the same flat-range-&gt;0.5 / clamped
    /// ``(x-min)/(max-min)`` convention as <see cref="MLDataProcessor.NormalizeCandles"/>'s price
    /// branch, generalized to one arbitrary channel; <see cref="ChannelNormalization.WindowZScore"/>
    /// is the same population Z-Score (ddof=0, degenerate-&gt;0.0) as
    /// <see cref="MLDataProcessor.NormalizeZScoreOhlcv"/>'s per-channel loop.
    /// </summary>
    private static void ApplyChannelNormalization(
        ReadOnlySpan<double> raw, ChannelNormalization normalization, Span<float> destination, int channelIndex, int channelCount)
    {
        int n = raw.Length;
        switch (normalization)
        {
            case ChannelNormalization.None:
                for (int i = 0; i < n; i++) destination[(i * channelCount) + channelIndex] = (float)raw[i];
                break;

            case ChannelNormalization.WindowMinMax:
            {
                double min = double.MaxValue, max = double.MinValue;
                for (int i = 0; i < n; i++)
                {
                    if (raw[i] < min) min = raw[i];
                    if (raw[i] > max) max = raw[i];
                }

                double range = max - min;
                for (int i = 0; i < n; i++)
                {
                    destination[(i * channelCount) + channelIndex] = range <= IMLDataProcessor.Epsilon
                        ? 0.5f
                        : (float)Math.Clamp((raw[i] - min) / range, 0.0, 1.0);
                }

                break;
            }

            case ChannelNormalization.WindowZScore:
            {
                double sum = 0.0;
                for (int i = 0; i < n; i++) sum += raw[i];
                double mu = sum / n;

                double sumSq = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double diff = raw[i] - mu;
                    sumSq += diff * diff;
                }

                double sigma = Math.Sqrt(sumSq / n);
                for (int i = 0; i < n; i++)
                {
                    destination[(i * channelCount) + channelIndex] = sigma <= IMLDataProcessor.Epsilon
                        ? 0.0f
                        : (float)((raw[i] - mu) / sigma);
                }

                break;
            }

            default:
                throw new InvalidOperationException($"Unsupported channel normalization '{normalization}'.");
        }
    }

    public async Task<PredictionResult> PredictAsync(IEnumerable<CandleData> candles)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PredictionService));
        // Once initialization has permanently failed (missing model file, or a shape/contract
        // mismatch that cannot self-heal), every attempt would extract features, rent a buffer,
        // run the full retry/backoff pipeline and still end at PredictionResult.Empty. Short-circuit
        // to the same result without the wasted work and the repeated warning logs.
        if (_permanentInitializationFailure && _failedModelId == _modelRegistry?.ActiveId)
            return PredictionResult.Empty;
        if (candles == null) return PredictionResult.Empty;

        // Own one history before any asynchronous initialization can let the caller mutate it.
        IReadOnlyList<CandleData> candleList;
        try { candleList = SnapshotHistory(candles); }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogWarning(ex, "Candle history changed while being copied; returning fallback.");
            return PredictionResult.Empty;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Candle history changed while being copied; returning fallback.");
            return PredictionResult.Empty;
        }
        int windowSize = _settings.PredictionWindowSize;

        if (candleList.Count < windowSize)
        {
            return PredictionResult.Empty;
        }

        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await InitializeAsync();

            if (_permanentInitializationFailure && _failedModelId == _modelRegistry?.ActiveId)
            {
                throw new InvalidOperationException("Prediction engine is not initialized.");
            }
            var runtime = AcquireRuntime()
                ?? throw new InvalidOperationException("Prediction engine is not initialized.");
            try
            {
                // Keep this generation alive through both feature construction and native Run.
                int runtimeWindow = runtime.Window;
                if (candleList.Count < runtimeWindow) return PredictionResult.Empty;
                if (runtime.Scaler is not null || runtime.Mode == PredictionFeatureMode.ComposedFeatures)
                    ValidateChronologicalHistory(candleList);
                int startIndex = candleList.Count - runtimeWindow;
                var scaler = runtime.Scaler;
                if (scaler is not null && startIndex < scaler.Lags.DefaultIfEmpty(0).Max())
                    throw new InvalidOperationException("Insufficient lag history for fixed preprocessing.");
                int featureCount = ComputeCheckedFeatureCount(
                    runtimeWindow, runtime.FeaturesPerBar, runtime.MaxTensorBytes);
                var buffer = ArrayPool<float>.Shared.Rent(featureCount);
                try
                {
                    if (scaler is not null)
                    {
                        var raw = new double[featureCount];
                        BuildFixedRawTensor(candleList, startIndex, scaler, runtime.FeatureSpec,
                            _indicatorFactory, raw);
                        scaler.Transform(raw, buffer.AsSpan(0, featureCount));
                    }
                    else switch (runtime.Mode)
                    {
                        case PredictionFeatureMode.LogReturn:
                            _dataProcessor.ComputeLogReturns(candleList, startIndex, runtimeWindow, buffer.AsSpan(0, featureCount));
                            break;
                        case PredictionFeatureMode.LogReturnOhlc:
                            _dataProcessor.ComputeLogReturnsOhlc(candleList, startIndex, runtimeWindow, buffer.AsSpan(0, featureCount));
                            break;
                        case PredictionFeatureMode.ZScoreStandardized:
                            _dataProcessor.NormalizeZScoreOhlcv(candleList, startIndex, runtimeWindow, buffer.AsSpan(0, featureCount));
                            break;
                        case PredictionFeatureMode.ZScoreOhlcvJoint:
                            _dataProcessor.ComputeJointZScoreOhlcv(candleList, startIndex, runtimeWindow, buffer.AsSpan(0, featureCount));
                            break;
                        case PredictionFeatureMode.ComposedFeatures:
                            BuildComposedTensor(candleList, startIndex, runtimeWindow, runtime.FeatureSpec!,
                                _indicatorFactory, buffer.AsSpan(0, featureCount));
                            break;
                        default:
                            _dataProcessor.NormalizeCandles(candleList, startIndex, runtimeWindow, buffer.AsSpan(0, featureCount));
                            break;
                    }

                    return await Task.Run(() => RunInference(runtime, buffer, featureCount), ct);
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(buffer, clearArray: true);
                }
            }
            finally
            {
                ReleaseRuntime(runtime);
            }
        });
    }

    /// <summary>
    /// Extracts a regression model's single output value, requiring exactly one value (the
    /// load-time contract already validated the output width, but this guards the runtime
    /// tensor too) and a finite result (02_ONNX.md Contract: "runtimeも有限値1個を検証" -
    /// a non-finite prediction cannot self-heal, so this fails closed like a shape mismatch).
    /// </summary>
    internal static float ExtractRegressionValue(ReadOnlySpan<float> outputSpan)
    {
        if (outputSpan.Length != 1)
        {
            throw new InvalidOperationException(
                $"ONNX model produced {outputSpan.Length} output value(s) but a regression model must produce exactly 1.");
        }

        float y = outputSpan[0];
        if (!float.IsFinite(y))
        {
            throw new InvalidOperationException($"ONNX regression model produced a non-finite output value ({y}).");
        }

        return y;
    }

    private const int MaxStackAllocClassCount = 32;

    private PredictionResult RunInference(RuntimeBundle runtime, float[] buffer, int featureCount)
    {
        // RunOptions belongs to the service; the gate prevents Dispose from freeing it mid-Run.
        // The runtime reference keeps this exact session alive after an active-generation swap.
        lock (_inferenceGate)
        {
            if (_disposed)
            {
                return PredictionResult.Empty;
            }

            return RunInferenceCore(runtime, buffer, featureCount);
        }
    }

    private PredictionResult RunInferenceCore(RuntimeBundle runtime, float[] buffer, int featureCount)
    {
        // Zero-copy tensor construction: wraps the ArrayPool-rented buffer directly, avoiding
        // the per-inference DenseTensor<float>/List<NamedOnnxValue> heap allocations of the legacy API.
        // Shape and node-name arrays belong to the same generation as the input tensor.
        using var inputValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer.AsMemory(0, featureCount), runtime.InputShape);

        using var results = runtime.Session.Run(
            runOptions: _runOptions,
            inputNames: runtime.InputNames,
            inputValues: new[] { inputValue },
            outputNames: runtime.OutputNames);

        // Raw stage: the tensor remains scoped to the native Run result. No UI meaning or
        // formatting is applied before DecodeSemantic validates its ONNX metadata contract.
        var raw = new RawPredictionOutput(results[0].GetTensorDataAsSpan<float>());
        return DecodeSemantic(runtime, raw);
    }

    private readonly ref struct RawPredictionOutput(ReadOnlySpan<float> values)
    {
        internal ReadOnlySpan<float> Values { get; } = values;
    }

    private PredictionResult DecodeSemantic(RuntimeBundle runtime, RawPredictionOutput raw)
    {
        var outputSpan = raw.Values;

        if (runtime.Target == TargetType.Regression)
        {
            float y = ExtractRegressionValue(outputSpan);
            return new PredictionResult(
                Label: string.Empty,
                Probability: float.NaN,
                Scores: Array.Empty<ClassScore>(),
                Confidence: float.NaN,
                Entropy: float.NaN,
                IsFallback: false)
            {
                IsRegression = true,
                PredictedLogReturn = y,
                OutputContract = runtime.OutputContract,
                ConfidenceType = ConfidenceType.None,
                ConfidenceValue = null,
                ModelId = runtime.ModelId,
            };
        }

        int classCount = runtime.ClassLabels.Length;
        if (outputSpan.Length != classCount)
        {
            // Contract guard: an undersized output would throw an opaque ArgumentOutOfRangeException
            // on the slice below, and an oversized output would be silently truncated into a
            // confident-looking prediction over the wrong class set. Fail closed so the resilience
            // pipeline surfaces it as a diagnosable Empty fallback instead.
            throw new InvalidOperationException(
                $"ONNX model produced {outputSpan.Length} output value(s) but {classCount} class label(s) are configured; the model output dimension and PredictionClassLabels must match.");
        }

        Span<float> rawOutput = classCount <= MaxStackAllocClassCount ? stackalloc float[classCount] : new float[classCount];
        outputSpan.Slice(0, classCount).CopyTo(rawOutput);

        float sum = 0f;
        bool hasNegative = false;
        bool hasInvalidProbability = false;
        for (int i = 0; i < classCount; i++)
        {
            sum += rawOutput[i];
            if (rawOutput[i] < 0f) hasNegative = true;
            if (!float.IsFinite(rawOutput[i]) || rawOutput[i] > 1f) hasInvalidProbability = true;
        }

        if (hasNegative || !float.IsFinite(sum)
            || MathF.Abs(1.0f - sum) > IMLDataProcessor.SoftmaxSumTolerance
            || hasInvalidProbability)
            throw new InvalidOperationException("ONNX output declared class probabilities but contains invalid values.");
        Span<float> probabilities = classCount <= MaxStackAllocClassCount ? stackalloc float[classCount] : new float[classCount];
        rawOutput.CopyTo(probabilities);

        var (confidence, entropy) = _dataProcessor.ComputeConfidenceAndEntropy(probabilities);

        int argmax = 0;
        float best = float.MinValue;
        var scores = new ClassScore[classCount];
        for (int i = 0; i < classCount; i++)
        {
            scores[i] = new ClassScore(runtime.ClassLabels[i], probabilities[i]);
            if (probabilities[i] > best)
            {
                best = probabilities[i];
                argmax = i;
            }
        }

        string label = confidence >= _settings.PredictionConfidenceThreshold
            ? runtime.ClassLabels[argmax]
            : "Unknown";

        return new PredictionResult(label, confidence, scores, confidence, entropy, IsFallback: false)
        {
            OutputContract = runtime.OutputContract,
            ConfidenceType = ConfidenceType.ClassProbability,
            ConfidenceValue = confidence,
            ModelId = runtime.ModelId,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Stop native calls, then retire the active generation. Existing preprocessors hold
        // their own references and release the retired session when they finish.
        lock (_inferenceGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var previous = _runtime;
            _runtime = null;
            Retire(previous);
            _runOptions.Dispose();
        }

        // _initLock is deliberately NOT disposed: SemaphoreSlim.Dispose() does not wake WaitAsync waiters that are already queued, and a
        // disposed semaphore cannot be Released, so an InitializeAsync call waiting behind a running initializer would hang forever. The
        // semaphore owns no unmanaged resource here (AvailableWaitHandle is never used), so leaving it to the GC loses nothing.
        GC.SuppressFinalize(this);
    }
}
