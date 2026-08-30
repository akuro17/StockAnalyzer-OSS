using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Fallback;
using Polly.Retry;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Implementation of <see cref="IPredictionService"/> using ONNX Runtime.
/// </summary>
public class PredictionService : IPredictionService, IDisposable
{
    private volatile bool _disposed;
    private readonly IStockAnalyzerSettings _settings;
    private readonly IMLDataProcessor _dataProcessor;
    private readonly ILogger<PredictionService> _logger;
    private readonly ResiliencePipeline<PredictionResult> _resiliencePipeline;
    private readonly System.Threading.SemaphoreSlim _initLock = new(1, 1);
    // Serializes native inference against Dispose(): RunInference holds this while calling
    // _session.Run, and Dispose() takes it before freeing _session / _runOptions, so a
    // concurrent teardown can no longer free native handles mid-Run (which surfaced as an
    // uncatchable AccessViolationException / process crash).
    private readonly object _inferenceGate = new();
    // Microsoft.ML.OnnxRuntime 1.24.3's Run(runOptions, ...) overload dereferences runOptions
    // in native interop, so a null throws NullReferenceException; reuse one default instance.
    private readonly RunOptions _runOptions = new();
    private const int FeaturesPerCandle = 5;

    /// <summary>
    /// Feature channels per bar for the configured <see cref="PredictionFeatureMode"/>.
    /// Single source of truth for the model input tensor's last dimension; mirrors the
    /// Python <c>dataset._MODE_CHANNELS</c> map.
    /// </summary>
    private int ResolvedFeaturesPerBar => _settings.PredictionFeatureMode switch
    {
        PredictionFeatureMode.LogReturn => 1,
        PredictionFeatureMode.LogReturnOhlc => 4,
        _ => FeaturesPerCandle,
    };

    private InferenceSession? _session;
    private string? _resolvedModelPath;
    private string? _resolvedInputNodeName;
    private string? _resolvedOutputNodeName;
    // Inference-invariant after model load; cached once to keep RunInference's steady-state
    // path allocation-free apart from the PredictionResult it returns.
    private long[]? _cachedInputShape;
    private string[]? _cachedInputNames;
    private string[]? _cachedOutputNames;
    private volatile bool _permanentInitializationFailure;

    public PredictionService(IStockAnalyzerSettings settings, IMLDataProcessor dataProcessor, ILogger<PredictionService>? logger = null)
    {
        _settings = settings;
        _dataProcessor = dataProcessor;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PredictionService>.Instance;
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
        if (_session != null || _permanentInitializationFailure) return;

        try
        {
            await _initLock.WaitAsync();
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently while waiting for the lock; nothing left to initialize.
            return;
        }

        try
        {
            if (_disposed || _session != null || _permanentInitializationFailure) return;
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
            try
            {
                _initLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed concurrently while this call held the lock; nothing left to release.
            }
        }
    }

    private void EnsureModelLoaded()
    {
        if (_session != null) return;

        var modelPath = Common.PathDiscovery.ResolvePredictionModelPath(_settings.PredictionModelPath);
        _resolvedModelPath = modelPath;
        if (!System.IO.File.Exists(modelPath))
        {
            // The model file's absence cannot self-heal without an app restart or config change,
            // so further InitializeAsync calls skip the redundant File.Exists + InferenceSession attempt.
            _permanentInitializationFailure = true;
            throw new System.IO.FileNotFoundException("ONNX model file not found.", modelPath);
        }

        // ONNX Runtime copies the options into the session at construction, so the
        // SessionOptions native handle can be released as soon as this method returns.
        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        var session = new InferenceSession(modelPath, sessionOptions);
        try
        {
            var inputName = ResolveNodeName(session.InputMetadata.Keys, _settings.PredictionInputNodeName);
            var outputName = ResolveNodeName(session.OutputMetadata.Keys, _settings.PredictionOutputNodeName);

            if (!session.InputMetadata.TryGetValue(inputName, out var inputMeta))
            {
                throw new InvalidOperationException($"Configured ONNX input node '{inputName}' is not present in the model.");
            }

            if (!session.OutputMetadata.TryGetValue(outputName, out var outputMeta))
            {
                throw new InvalidOperationException($"Configured ONNX output node '{outputName}' is not present in the model.");
            }

            int featuresPerBar = ResolvedFeaturesPerBar;
            ValidateModelContract(
                inputMeta.IsTensor, inputMeta.Dimensions,
                outputMeta.IsTensor, outputMeta.Dimensions,
                _settings.PredictionWindowSize, featuresPerBar, _settings.PredictionClassLabels.Count,
                inputName, outputName);

            // Semantic contract: the model's embedded metadata_props (feature_mode / window_size /
            // class_order) must match the running configuration. A mismatch here is as
            // unrecoverable as a shape mismatch, so the surrounding catch treats it identically.
            PredictionModelMetadata.Validate(
                session.ModelMetadata.CustomMetadataMap,
                _settings.PredictionFeatureMode,
                _settings.PredictionWindowSize,
                _settings.PredictionClassLabels,
                _logger);

            lock (_inferenceGate)
            {
                if (_disposed)
                {
                    // Disposed while this load was in flight: drop the session instead of
                    // publishing a native handle that Dispose() has already stopped tracking.
                    session.Dispose();
                    return;
                }

                _resolvedInputNodeName = inputName;
                _resolvedOutputNodeName = outputName;
                _cachedInputNames = new[] { inputName };
                _cachedOutputNames = new[] { outputName };
                _cachedInputShape = new long[] { 1, _settings.PredictionWindowSize, featuresPerBar };
                _session = session;
            }
        }
        catch
        {
            // A shape/contract mismatch cannot self-heal without a model or config change, so mirror
            // the FileNotFound path: dispose the session and stop retrying on every PredictAsync call.
            session.Dispose();
            _permanentInitializationFailure = true;
            throw;
        }
    }

    /// <summary>
    /// Validates the loaded ONNX model against the configured inference contract:
    /// input tensor rank 3 <c>[batch, WindowSize, FeaturesPerBar]</c> and output tensor rank 2
    /// <c>[batch, ClassCount]</c>. Dimensions reported as -1 are dynamic and accepted; fixed
    /// dimensions must match. Every violation throws <see cref="InvalidOperationException"/>.
    /// </summary>
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

    public async Task<PredictionResult> PredictAsync(IEnumerable<CandleData> candles)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PredictionService));
        // Once initialization has permanently failed (missing model file, or a shape/contract
        // mismatch that cannot self-heal), every attempt would extract features, rent a buffer,
        // run the full retry/backoff pipeline and still end at PredictionResult.Empty. Short-circuit
        // to the same result without the wasted work and the repeated warning logs.
        if (_permanentInitializationFailure) return PredictionResult.Empty;
        if (candles == null) return PredictionResult.Empty;

        // Avoid multiple enumerations and extra allocations
        var candleList = candles as IReadOnlyList<CandleData> ?? candles.ToList();
        int windowSize = _settings.PredictionWindowSize;

        if (candleList.Count < windowSize)
        {
            return PredictionResult.Empty;
        }

        return await _resiliencePipeline.ExecuteAsync(async ct =>
        {
            await InitializeAsync();

            if (_session == null || _resolvedInputNodeName == null || _resolvedOutputNodeName == null)
            {
                throw new InvalidOperationException("Prediction engine is not initialized.");
            }

            // Zero-allocation slice logic: use the last 'windowSize' candles
            int startIndex = candleList.Count - windowSize;
            int featuresPerBar = ResolvedFeaturesPerBar;
            int featureCount = windowSize * featuresPerBar;
            var buffer = ArrayPool<float>.Shared.Rent(featureCount);
            try
            {
                switch (_settings.PredictionFeatureMode)
                {
                    case PredictionFeatureMode.LogReturn:
                        _dataProcessor.ComputeLogReturns(candleList, startIndex, windowSize, buffer.AsSpan(0, featureCount));
                        break;
                    case PredictionFeatureMode.LogReturnOhlc:
                        _dataProcessor.ComputeLogReturnsOhlc(candleList, startIndex, windowSize, buffer.AsSpan(0, featureCount));
                        break;
                    case PredictionFeatureMode.ZScoreStandardized:
                        _dataProcessor.NormalizeZScoreOhlcv(candleList, startIndex, windowSize, buffer.AsSpan(0, featureCount));
                        break;
                    case PredictionFeatureMode.ZScoreOhlcvJoint:
                        _dataProcessor.ComputeJointZScoreOhlcv(candleList, startIndex, windowSize, buffer.AsSpan(0, featureCount));
                        break;
                    default:
                        _dataProcessor.NormalizeCandles(candleList, startIndex, windowSize, buffer.AsSpan(0, featureCount));
                        break;
                }

                return await Task.Run(() => RunInference(buffer, featureCount), ct);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer, clearArray: true);
            }
        });
    }

    private const int MaxStackAllocClassCount = 32;

    private PredictionResult RunInference(float[] buffer, int featureCount)
    {
        // Hold the inference gate across the whole native call so a concurrent Dispose()
        // cannot free _session / _runOptions mid-Run. On a disposal race, degrade to the
        // same Empty result the resilience Fallback returns instead of crashing.
        lock (_inferenceGate)
        {
            if (_disposed || _session is null)
            {
                return PredictionResult.Empty;
            }

            return RunInferenceCore(buffer, featureCount);
        }
    }

    private PredictionResult RunInferenceCore(float[] buffer, int featureCount)
    {
        // Zero-copy tensor construction: wraps the ArrayPool-rented buffer directly, avoiding
        // the per-inference DenseTensor<float>/List<NamedOnnxValue> heap allocations of the legacy API.
        // Shape and node-name arrays are load-time invariants reused from cached fields.
        using var inputValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, buffer.AsMemory(0, featureCount), _cachedInputShape!);

        using var results = _session!.Run(
            runOptions: _runOptions,
            inputNames: _cachedInputNames!,
            inputValues: new[] { inputValue },
            outputNames: _cachedOutputNames!);

        var outputSpan = results[0].GetTensorDataAsSpan<float>();

        int classCount = _settings.PredictionClassLabels.Count;
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
        for (int i = 0; i < classCount; i++)
        {
            sum += rawOutput[i];
            if (rawOutput[i] < 0f) hasNegative = true;
        }

        bool needsSoftmax = hasNegative || MathF.Abs(1.0f - sum) > IMLDataProcessor.SoftmaxSumTolerance;

        Span<float> probabilities = classCount <= MaxStackAllocClassCount ? stackalloc float[classCount] : new float[classCount];
        if (needsSoftmax)
        {
            _dataProcessor.ComputeSoftmax(rawOutput, probabilities);
        }
        else
        {
            rawOutput.CopyTo(probabilities);
        }

        var (confidence, entropy) = _dataProcessor.ComputeConfidenceAndEntropy(probabilities);

        int argmax = 0;
        float best = float.MinValue;
        var scores = new ClassScore[classCount];
        for (int i = 0; i < classCount; i++)
        {
            scores[i] = new ClassScore(_settings.PredictionClassLabels[i], probabilities[i]);
            if (probabilities[i] > best)
            {
                best = probabilities[i];
                argmax = i;
            }
        }

        string label = confidence >= _settings.PredictionConfidenceThreshold
            ? _settings.PredictionClassLabels[argmax]
            : "Unknown";

        return new PredictionResult(label, confidence, scores, confidence, entropy, IsFallback: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Take the inference gate so this waits for any in-flight RunInference to finish and
        // blocks a new one from entering before the native handles are freed.
        lock (_inferenceGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _session?.Dispose();
            _session = null;
            _runOptions.Dispose();
        }

        _initLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
