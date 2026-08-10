using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// Implementation of <see cref="IPredictionService"/> using ONNX Runtime.
/// </summary>
public class PredictionService : IPredictionService, IDisposable
{
    private bool _disposed;
    private readonly IStockAnalyzerSettings _settings;
    private readonly IMLDataProcessor _dataProcessor;
    private readonly ILogger<PredictionService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly System.Threading.SemaphoreSlim _initLock = new(1, 1);
    private const int FeaturesPerCandle = 5;
    private InferenceSession? _session;

    public PredictionService(IStockAnalyzerSettings settings, IMLDataProcessor dataProcessor, ILogger<PredictionService>? logger = null)
    {
        _settings = settings;
        _dataProcessor = dataProcessor;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PredictionService>.Instance;
        _resiliencePipeline = BuildResiliencePipeline();
    }

    private ResiliencePipeline BuildResiliencePipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(500),
                OnRetry = args =>
                {
                    _logger.LogWarning("Prediction attempt {Attempt} failed: {Error}. Retrying...", args.AttemptNumber + 1, args.Outcome.Exception?.Message);
                    return default;
                },
                ShouldHandle = new PredicateBuilder()
                    .Handle<OnnxRuntimeException>()
                    .Handle<System.IO.IOException>()
            })
            .Build();
    }

    public async Task InitializeAsync()
    {
        if (_session != null) return;

        await _initLock.WaitAsync();
        try
        {
            if (_session != null) return;
            // Load model asynchronously to prevent UI thread blocking
            await Task.Run(() => EnsureModelLoaded());
            _logger.LogInformation("ONNX model loaded successfully from {Path}.", _settings.PredictionModelPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ONNX model from {Path}.", _settings.PredictionModelPath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private void EnsureModelLoaded()
    {
        if (_session != null) return;

        var modelPath = System.IO.Path.Combine(AppContext.BaseDirectory, _settings.PredictionModelPath);
        if (!System.IO.File.Exists(modelPath))
        {
            throw new System.IO.FileNotFoundException("ONNX model file not found.", modelPath);
        }

        _session = new InferenceSession(modelPath);
    }

    public async Task<PredictionResult> PredictAsync(IEnumerable<CandleData> candles)
    {
        if (candles == null) return new PredictionResult("Null Input", 0f, new Dictionary<string, float>());

        try
        {
            // Avoid multiple enumerations and extra allocations
            var candleList = candles as IReadOnlyList<CandleData> ?? candles.ToList();
            int windowSize = _settings.PredictionWindowSize;

            if (candleList.Count < windowSize)
            {
                return new PredictionResult("Insufficient Data", 0f, new Dictionary<string, float>());
            }

            // Zero-allocation slice logic: use the last 'windowSize' candles
            int startIndex = candleList.Count - windowSize;
            int featureCount = windowSize * FeaturesPerCandle;
            var buffer = ArrayPool<float>.Shared.Rent(featureCount);

            try
            {
                // Normalize using the windowed overload (no Skip().ToList() allocation)
                _dataProcessor.NormalizeCandles(candleList, startIndex, windowSize, buffer.AsSpan(0, featureCount));

                // ONNX Inference with Resilience
                return await _resiliencePipeline.ExecuteAsync(async ct =>
                {
                    await InitializeAsync();

                    if (_session == null)
                    {
                        throw new InvalidOperationException("Prediction engine is not initialized.");
                    }

                    return await Task.Run(() =>
                    {
                        // DenseTensor wraps the memory (no copy)
                        var inputTensor = new DenseTensor<float>(buffer.AsMemory(0, featureCount), new[] { 1, windowSize, FeaturesPerCandle });

                        var inputs = new List<NamedOnnxValue>
                        {
                            NamedOnnxValue.CreateFromTensor("input", inputTensor)
                        };

                        using var results = _session!.Run(inputs);
                        
                        // Assuming model output is a probability distribution [Up, Down, Neutral]
                        var outputTensor = results.First().AsTensor<float>();
                        var scores = new Dictionary<string, float>
                        {
                            { "Up", outputTensor[0, 0] },
                            { "Down", outputTensor[0, 1] },
                            { "Neutral", outputTensor[0, 2] }
                        };

                        var maxEntry = scores.OrderByDescending(x => x.Value).First();
                        return new PredictionResult(maxEntry.Key, maxEntry.Value, scores);
                    }, ct);
                });
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prediction error occurred.");
            return new PredictionResult($"Error: {ex.Message}", 0f, new Dictionary<string, float>());
        }
    }


    public void Dispose()
    {
        if (!_disposed)
        {
            _session?.Dispose();
            _session = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
