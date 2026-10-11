using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>
/// <see cref="IAttentionService"/> over ONNX Runtime. The input tensor is built exactly as the prediction builds it: the same <c>internal static</c> tensor
/// builders (<c>PredictionService.BuildFixedRawTensor</c> / <c>BuildComposedTensor</c>, read only; <c>EnsemblePredictionService</c> reuses them the same way) and
/// the same settings (window, feature mode, feature spec), so the attention is of the very input the displayed prediction saw.
/// </summary>
public sealed class AttentionService : IAttentionService
{
    private readonly IModelGenerationRegistry _registry;
    private readonly IStockAnalyzerSettings _settings;
    private readonly IMLDataProcessor _processor;
    private readonly IIndicatorFactory _indicators;
    private readonly ILogger<AttentionService> _logger;
    private readonly BoundedLruCache<string, ProductionContext> _contexts;

    public AttentionService(IModelGenerationRegistry registry, IStockAnalyzerSettings settings, IMLDataProcessor processor,
        IIndicatorFactory? indicators = null, ILogger<AttentionService>? logger = null)
        : this(registry, settings, processor, indicators, logger, ModelExplanationLimits.MaxCachedGenerations)
    {
    }

    /// <summary>A smaller capacity than the contract bound, so tests can exercise eviction.</summary>
    internal AttentionService(IModelGenerationRegistry registry, IStockAnalyzerSettings settings, IMLDataProcessor processor,
        IIndicatorFactory? indicators, ILogger<AttentionService>? logger, int cacheCapacity)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _indicators = indicators ?? IndicatorFactory.Default;
        _logger = logger ?? NullLogger<AttentionService>.Instance;
        _contexts = new BoundedLruCache<string, ProductionContext>(cacheCapacity, StringComparer.Ordinal);
    }

    /// <summary>
    /// What the model contributes to the input tensor: managed values only (no native handle), so it can be cached. The feature mode and spec are the
    /// configured ones for the single active model and the model's own metadata for an ensemble member.
    /// </summary>
    private sealed record ProductionContext(string FeatureContractHash, int Window, int Channels, FixedScaler? Scaler,
        PredictionFeatureMode Mode, FeatureSpec? Spec);

    /// <summary>Key prefix of the cached contexts of ensemble members, so a member context never answers a single-model request (and vice versa).</summary>
    private const string MemberContextPrefix = "member:";
    private const string SingleContextSeparator = "|";

    public Task<AttentionResult> ComputeAsync(AttentionRequestKey key, IReadOnlyList<CandleData> history, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(history);
        return Task.Run(() => Compute(key, history, ownContract: false, cancellationToken), cancellationToken);
    }

    public Task<AttentionResult> ComputeMemberAsync(AttentionRequestKey key, IReadOnlyList<CandleData> history, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(history);
        return Task.Run(() => Compute(key, history, ownContract: true, cancellationToken), cancellationToken);
    }

    private AttentionResult Compute(AttentionRequestKey key, IReadOnlyList<CandleData> source, bool ownContract, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var lease = _registry.Acquire(key.ModelId);
        var manifest = lease is null ? null : _registry.GetManifest(key.ModelId);
        if (lease is null || manifest is null) return AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoModel);
        if (!manifest.Timeframe.Equals(key.Timeframe, StringComparison.OrdinalIgnoreCase))
            return AttentionResult.Unsupported(AttentionContract.UnsupportedReason.Timeframe);
        var sidecarPath = lease.ModelPath + AttentionContract.SidecarSuffix;
        if (!File.Exists(sidecarPath)) return AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoSidecar);

        try
        {
            using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
            using var session = new InferenceSession(sidecarPath, options);
            var binding = ReadBinding(session.ModelMetadata.CustomMetadataMap, manifest.ModelHash, out var refusal);
            if (binding is null) return AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoSidecar, refusal);
            var context = ContextFor(key.ModelId, lease.ModelPath, ownContract);
            if (context.Window != binding.Window || (!ownContract && _settings.PredictionWindowSize != context.Window)
                || context.FeatureContractHash != binding.FeatureContractHash)
                return AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoSidecar, "the sidecar does not match the model's feature contract or the configured window");
            var shapeProblem = CheckShapes(session, binding, context);
            if (shapeProblem is not null) return AttentionResult.Unsupported(AttentionContract.UnsupportedReason.NoSidecar, shapeProblem);

            var history = Snapshot(source);
            var historyProblem = CheckHistory(history, context);
            if (historyProblem is not null) return AttentionResult.Failed(historyProblem);
            ct.ThrowIfCancellationRequested();

            var tensor = BuildTensor(history, context);
            var weights = Run(session, tensor, binding, context, out var runProblem);
            if (weights is null) return AttentionResult.Failed(runProblem ?? "the attention output is invalid");
            ct.ThrowIfCancellationRequested();

            var last = Aggregate(weights, binding.Heads, context.Window, out var aggregateProblem);
            if (last is null) return AttentionResult.Failed(aggregateProblem ?? "the attention of the last bar is not usable");
            var timestamps = ImmutableArray.CreateRange(history.Skip(history.Length - context.Window).Select(c => c.Timestamp));
            var relative = new float[last.Length];
            float max = last.Max();
            for (int i = 0; i < relative.Length; i++) relative[i] = last[i] / max;
            return AttentionResult.Ready(new AttentionSnapshot(key, binding.Heads, timestamps, ImmutableArray.Create(last), ImmutableArray.Create(relative)));
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidDataException or InvalidOperationException or ArgumentException or OverflowException)
        {
            _logger.LogWarning(ex, "Attention could not be computed for generation {ModelId}.", key.ModelId);
            return AttentionResult.Failed(ex.Message);
        }
    }

    private sealed record Binding(int Window, int Heads, string FeatureContractHash);

    /// <summary>The verified binding of a sidecar, or <see langword="null"/> and the reason it is refused (it is then "unsupported", not an error).</summary>
    private static Binding? ReadBinding(IReadOnlyDictionary<string, string> metadata, string parentHash, out string? refusal)
    {
        refusal = null;
        var present = metadata.Keys.Where(k => k.StartsWith(AttentionContract.MetadataPrefix, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (!present.SetEquals(AttentionContract.BindingKeys)) { refusal = "the sidecar's attention metadata keys are not exactly the contract"; return null; }
        if (metadata[AttentionContract.SchemaVersionKey] != AttentionContract.SchemaVersion.ToString(CultureInfo.InvariantCulture)
            || metadata[AttentionContract.LayoutKey] != AttentionContract.Layout || metadata[AttentionContract.MaskKey] != AttentionContract.Mask
            || metadata[AttentionContract.UnitKey] != AttentionContract.Unit) { refusal = "unsupported schema, layout, mask or unit"; return null; }
        if (!string.Equals(metadata[AttentionContract.ParentSha256Key], parentHash, StringComparison.OrdinalIgnoreCase))
        { refusal = "the sidecar belongs to another model (parent SHA-256 differs)"; return null; }
        if (!int.TryParse(metadata[AttentionContract.WindowSizeKey], NumberStyles.None, CultureInfo.InvariantCulture, out int window)
            || window < AttentionContract.MinWindow || window > AttentionContract.MaxWindow) { refusal = "window_size is out of range"; return null; }
        if (!int.TryParse(metadata[AttentionContract.HeadsKey], NumberStyles.None, CultureInfo.InvariantCulture, out int heads)
            || heads < 1 || heads > AttentionContract.MaxHeads) { refusal = "heads is out of range"; return null; }
        return new Binding(window, heads, metadata[AttentionContract.FeatureContractHashKey]);
    }

    private ProductionContext ContextFor(string modelId, string modelPath, bool ownContract)
    {
        // A single-model context also depends on the configured feature mode and spec, so its key carries the contract fingerprint; a member
        // context is read from the model's own metadata only.
        var cacheKey = ownContract ? MemberContextPrefix + modelId : modelId + SingleContextSeparator + PredictionContractFingerprint.Compute(_settings);
        if (_contexts.TryGet(cacheKey, out var cached)) return cached;
        using var options = new SessionOptions();
        using var session = new InferenceSession(modelPath, options);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        int window;
        PredictionFeatureMode mode;
        FeatureSpec? spec;
        if (ownContract)
        {
            (mode, window, spec) = PredictionModelMetadata.ReadOwnFeatureContract(metadata);
        }
        else
        {
            if (!metadata.TryGetValue(PredictionModelMetadata.WindowSizeKey, out var windowText)
                || !int.TryParse(windowText, NumberStyles.None, CultureInfo.InvariantCulture, out window))
                throw new InvalidDataException("The production model has no window size.");
            mode = _settings.PredictionFeatureMode;
            spec = mode == PredictionFeatureMode.ComposedFeatures ? _settings.PredictionFeatureSpec : null;
        }

        var scaler = PredictionModelMetadata.RequiresFixedScaler(metadata) ? FixedScaler.Load(modelPath, metadata) : null;
        int channels = scaler?.Statistics.Length ?? PredictionService.ResolveFeaturesPerBar(mode, spec);
        var context = new ProductionContext(FixedScaler.ComputeContractHash(metadata), window, channels, scaler, mode, spec);
        _contexts.TryAdd(cacheKey, context);
        return context;
    }

    private static string? CheckShapes(InferenceSession session, Binding binding, ProductionContext context)
    {
        if (session.InputMetadata.Count != 1 || !session.InputMetadata.TryGetValue(AttentionContract.InputName, out var input)) return "the sidecar must have exactly the input 'input'";
        if (session.OutputMetadata.Count != 1 || !session.OutputMetadata.TryGetValue(AttentionContract.OutputName, out var output)) return "the sidecar must have exactly the output 'attention'";
        string? Check(NodeMetadata meta, int[] expected, string what)
        {
            if (!meta.IsTensor || meta.ElementType != typeof(float)) return $"{what} must be a float32 tensor";
            var dims = meta.Dimensions;
            if (dims is null || dims.Length != expected.Length) return $"{what} has the wrong rank";
            if (dims[0] is not (1 or -1)) return $"{what} must have batch dimension 1 or dynamic";
            for (int i = 1; i < dims.Length; i++)
                if (dims[i] > 0 && dims[i] != expected[i]) return $"{what} dimension {i} is {dims[i]}, expected {expected[i]}";
            return null;
        }

        return Check(input, new[] { 1, context.Window, context.Channels }, "the sidecar input")
            ?? Check(output, new[] { 1, binding.Heads, context.Window, context.Window }, "the sidecar output");
    }

    private static CandleData[] Snapshot(IReadOnlyList<CandleData> history)
    {
        var copy = new CandleData[history.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = history[i];
        return copy;
    }

    private static string? CheckHistory(CandleData[] history, ProductionContext context)
    {
        int lag = context.Scaler is null ? 0 : context.Scaler.Lags.DefaultIfEmpty(0).Max();
        if (history.Length < context.Window || history.Length - context.Window < lag) return "the history is shorter than the model window";
        for (int i = 1; i < history.Length; i++)
            if (history[i].Timestamp <= history[i - 1].Timestamp) return "candle timestamps must be strictly increasing";
        return null;
    }

    /// <summary>The same input tensor the displayed prediction was made from.</summary>
    private float[] BuildTensor(CandleData[] history, ProductionContext context)
    {
        int start = history.Length - context.Window;
        long bytes = checked((long)context.Window * context.Channels * sizeof(float));
        long maxBytes = (long)_settings.PredictionMaxComposedTensorSizeMB * 1024 * 1024;
        if (bytes > maxBytes) throw new InvalidOperationException("The attention input tensor exceeds the configured tensor bound.");
        var tensor = new float[context.Window * context.Channels];
        var spec = context.Spec;
        if (context.Scaler is { } scaler)
        {
            var raw = new double[tensor.Length];
            PredictionService.BuildFixedRawTensor(history, start, scaler, spec, _indicators, raw);
            scaler.Transform(raw, tensor);
            return tensor;
        }

        switch (context.Mode)
        {
            case PredictionFeatureMode.LogReturn: _processor.ComputeLogReturns(history, start, context.Window, tensor); break;
            case PredictionFeatureMode.LogReturnOhlc: _processor.ComputeLogReturnsOhlc(history, start, context.Window, tensor); break;
            case PredictionFeatureMode.ZScoreStandardized: _processor.NormalizeZScoreOhlcv(history, start, context.Window, tensor); break;
            case PredictionFeatureMode.ZScoreOhlcvJoint: _processor.ComputeJointZScoreOhlcv(history, start, context.Window, tensor); break;
            case PredictionFeatureMode.ComposedFeatures:
                PredictionService.BuildComposedTensor(history, start, context.Window, spec!, _indicators, tensor); break;
            default: _processor.NormalizeCandles(history, start, context.Window, tensor); break;
        }

        return tensor;
    }

    private static float[]? Run(InferenceSession session, float[] tensor, Binding binding, ProductionContext context, out string? problem)
    {
        problem = null;
        long[] shape = { 1, context.Window, context.Channels };
        using var input = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, tensor.AsMemory(), shape);
        using var runOptions = new RunOptions();
        using var results = session.Run(runOptions, new[] { AttentionContract.InputName }, new[] { input }, new[] { AttentionContract.OutputName });
        if (results.Count != 1 || !results[0].IsTensor) { problem = "the sidecar did not return one tensor"; return null; }
        var info = results[0].GetTensorTypeAndShape();
        if (info.ElementDataType != TensorElementType.Float || info.Shape.Length != AttentionContract.OutputRank || info.Shape[0] != 1
            || info.Shape[1] != binding.Heads || info.Shape[2] != context.Window || info.Shape[3] != context.Window)
        { problem = "the attention output does not have shape [1, heads, window, window]"; return null; }
        var values = results[0].GetTensorDataAsSpan<float>().ToArray();
        int w = context.Window;
        for (int h = 0; h < binding.Heads; h++)
        {
            for (int q = 0; q < w; q++)
            {
                float sum = 0f;
                for (int k = 0; k < w; k++)
                {
                    float v = values[((h * w) + q) * w + k];
                    if (!float.IsFinite(v) || v < 0f || (k > q && v != 0f)) { problem = "the attention output breaks the causal mask or is not finite"; return null; }
                    sum += v;
                }

                if (MathF.Abs(sum - 1f) > AttentionContract.RowSumTolerance) { problem = "an attention row does not sum to 1"; return null; }
            }
        }

        return values;
    }

    /// <summary>The mean over the heads of the LAST query row (<c>weights[h, window - 1, k]</c>); <see langword="null"/> when its maximum is not positive.</summary>
    private static float[]? Aggregate(float[] weights, int heads, int window, out string? problem)
    {
        problem = null;
        var mean = new float[window];
        int q = window - 1;
        for (int k = 0; k < window; k++)
        {
            double sum = 0;
            for (int h = 0; h < heads; h++) sum += weights[((h * window) + q) * window + k];
            mean[k] = (float)(sum / heads);
        }

        if (!(mean.Max() > 0f)) { problem = "the maximum attention weight is not positive"; return null; }
        return mean;
    }
}
