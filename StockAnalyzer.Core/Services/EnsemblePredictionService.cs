using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

/// <summary>Runs each immutable classification generation against the same candle history.</summary>
public sealed class EnsemblePredictionService : IPredictionService, IDisposable
{
    public string? ActiveModelId => _registry.ActiveId;

    private readonly PredictionService _single;
    private readonly IEnsembleSettingsManager _ensembles;
    private readonly IModelGenerationRegistry _registry;
    private readonly IStockAnalyzerSettings _settings;
    private readonly IMLDataProcessor _processor;
    private readonly IIndicatorFactory _indicators;
    private readonly ILogger<EnsemblePredictionService> _logger;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private RuntimeBundle? _runtime;
    private EnsembleSpec? _runtimeSpec;
    private bool _disposed;
    private bool _loaded;

    public EnsemblePredictionService(PredictionService single, IEnsembleSettingsManager ensembles,
        IModelGenerationRegistry registry, IStockAnalyzerSettings settings, IMLDataProcessor processor,
        IIndicatorFactory? indicators = null, ILogger<EnsemblePredictionService>? logger = null)
    {
        _single = single;
        _ensembles = ensembles;
        _registry = registry;
        _settings = settings;
        _processor = processor;
        _indicators = indicators ?? IndicatorFactory.Default;
        _logger = logger ?? NullLogger<EnsemblePredictionService>.Instance;
    }

    public async Task InitializeAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_loaded)
        {
            await _initialization.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_loaded)
                {
                    await _ensembles.LoadAsync().ConfigureAwait(false);
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _loaded = true;
                }
            }
            finally { _initialization.Release(); }
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ensembles.Current is null) await _single.InitializeAsync().ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public async Task<PredictionResult> PredictAsync(IEnumerable<CandleData> candles)
    {
        if (candles is null) return PredictionResult.Empty;
        CandleData[] history;
        try
        {
            history = PredictionService.SnapshotHistory(candles);
            PredictionService.ValidateChronologicalHistory(history);
            await InitializeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ensemble input or settings could not be used; returning fallback.");
            return PredictionResult.Empty;
        }
        var spec = _ensembles.Current;
        if (spec is null)
        {
            await _runtimeGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed) return PredictionResult.Empty;
                _runtime?.Dispose(); _runtime = null; _runtimeSpec = null;
            }
            finally { _runtimeGate.Release(); }
            var result = await _single.PredictAsync(history).ConfigureAwait(false);
            return _disposed ? PredictionResult.Empty : result;
        }
        await _runtimeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!ReferenceEquals(_runtimeSpec, spec))
            {
                _runtime?.Dispose();
                _runtime = null;
                _runtimeSpec = null;
                _runtime = await Task.Run(() => BuildRuntime(spec)).ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(_disposed, this);
                _runtimeSpec = spec;
            }
            return await Task.Run(() => PredictBundle(spec, _runtime!, history)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ensemble prediction failed; returning fallback.");
            return PredictionResult.Empty;
        }
        finally { _runtimeGate.Release(); }
    }

    private PredictionResult PredictBundle(EnsembleSpec spec, RuntimeBundle bundle, IReadOnlyList<CandleData> history)
    {
        var rows = new float[bundle.Members.Count][];
        for (int i = 0; i < rows.Length; i++)
        {
            var member = bundle.Members[i];
            var raw = InferMember(member, history);
            rows[i] = member.CanonicalIndices.Select(index => raw[index]).ToArray();
        }
        return Combine(spec, bundle.Canonical, rows, _settings.PredictionConfidenceThreshold, _processor);
    }

    private RuntimeBundle BuildRuntime(EnsembleSpec spec)
    {
        EnsembleSettingsManager.ValidateGenerations(spec, _registry);
        var canonical = _registry.GetManifest(spec.Members[0].ModelId)!.ClassOrder.Split(',');
        var bundle = new RuntimeBundle(canonical);
        try
        {
            foreach (var member in spec.Members)
            {
                var manifest = _registry.GetManifest(member.ModelId)!;
                var lease = _registry.Acquire(member.ModelId)
                    ?? throw new InvalidDataException($"Generation '{member.ModelId}' is unavailable.");
                try
                {
                    var runtime = LoadMember(lease, manifest, canonical);
                    bundle.Members.Add(runtime);
                }
                catch { lease.Dispose(); throw; }
            }
            return bundle;
        }
        catch { bundle.Dispose(); throw; }
    }

    private MemberRuntime LoadMember(ModelGenerationLease lease, ModelGenerationManifest manifest, string[] canonical)
    {
        var session = new InferenceSession(lease.ModelPath);
        try
        {
            var metadata = session.ModelMetadata.CustomMetadataMap;
            var scaler = PredictionModelMetadata.RequiresFixedScaler(metadata)
                ? FixedScaler.Load(lease.ModelPath, metadata) : null;
            var mode = PredictionModelMetadata.ParseFeatureMode(metadata[PredictionModelMetadata.FeatureModeKey])
                ?? throw new InvalidDataException("Unknown feature mode.");
            int window = int.Parse(metadata[PredictionModelMetadata.WindowSizeKey], System.Globalization.CultureInfo.InvariantCulture);
            if (window <= 0) throw new InvalidDataException("Invalid model window.");
            FeatureSpec? featureSpec = null;
            if (mode == PredictionFeatureMode.ComposedFeatures)
                featureSpec = JsonSerializer.Deserialize<FeatureSpec>(metadata[PredictionModelMetadata.FeatureSpecKey],
                    TrainingConfigJson.Options);
            var labels = manifest.ClassOrder.Split(',');
            PredictionModelMetadata.Validate(metadata, mode, window, labels, _logger,
                featureSpec is null ? null : JsonSerializer.Serialize(featureSpec, TrainingConfigJson.Options));
            if (mode == PredictionFeatureMode.ComposedFeatures)
                PredictionService.ValidateComposedFeatureSpec(featureSpec, _settings.PredictionMaxComposedChannels, _indicators);
            int channels = scaler?.Statistics.Length ?? PredictionService.ResolveFeaturesPerBar(mode, featureSpec);
            string input = PredictionService.ResolveNodeName(session.InputMetadata.Keys, null);
            string output = PredictionService.ResolveNodeName(session.OutputMetadata.Keys, null);
            var inputMeta = session.InputMetadata[input];
            var outputMeta = session.OutputMetadata[output];
            PredictionService.ValidateModelContract(inputMeta, outputMeta,
                window, channels, labels.Length, input, output);
            int count = PredictionService.ComputeCheckedFeatureCount(window, channels,
                (long)_settings.PredictionMaxComposedTensorSizeMB * TrainingResourceOverrides.MiB);
            var map = canonical.Select(label => Array.IndexOf(labels, label)).ToArray();
            if (map.Any(index => index < 0)) throw new InvalidDataException("Label mapping is incomplete.");
            return new MemberRuntime(lease, session, mode, window, channels, count, featureSpec, scaler,
                input, output, map);
        }
        catch { session.Dispose(); throw; }
    }

    internal static PredictionResult Combine(EnsembleSpec spec, IReadOnlyList<string> canonical,
        IReadOnlyList<float[]> rows, float threshold, IMLDataProcessor processor)
    {
        spec.Validate();
        if (rows.Count != spec.Members.Count || canonical.Count == 0
            || canonical.Any(string.IsNullOrWhiteSpace)
            || canonical.Distinct(StringComparer.Ordinal).Count() != canonical.Count)
            throw new InvalidDataException("Invalid ensemble result shape or labels.");
        var weighted = new double[canonical.Count];
        var probabilities = new float[canonical.Count];
        Aggregate(spec, rows, weighted, probabilities);
        var (confidence, entropy) = processor.ComputeConfidenceAndEntropy(probabilities);
        int best = 0;
        for (int j = 1; j < probabilities.Length; j++)
            if (probabilities[j] > probabilities[best]) best = j;
        var scores = canonical.Select((label, index) => new ClassScore(label, probabilities[index])).ToArray();
        return new PredictionResult(confidence >= threshold ? canonical[best] : "Unknown",
            confidence, Array.AsReadOnly(scores), confidence, entropy);
    }

    /// <summary>Allocation-free inner aggregation over caller-owned scratch buffers.</summary>
    internal static void Aggregate(EnsembleSpec spec, IReadOnlyList<float[]> rows,
        Span<double> weighted, Span<float> probabilities)
    {
        if (weighted.Length != probabilities.Length || rows.Count != spec.Members.Count)
            throw new InvalidDataException("Invalid ensemble aggregation shape.");
        weighted.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Length != weighted.Length) throw new InvalidDataException("Member output width mismatch.");
            double rowSum = 0;
            for (int j = 0; j < weighted.Length; j++)
            {
                float value = rows[i][j];
                if (!float.IsFinite(value) || value is < 0 or > 1)
                    throw new InvalidDataException("Member probability is outside [0,1] or nonfinite.");
                rowSum += value;
                weighted[j] += spec.Members[i].Weight * value;
            }
            if (Math.Abs(rowSum - 1) > EnsembleSpec.SumTolerance)
                throw new InvalidDataException("Member probabilities do not sum to one.");
        }
        double total = 0;
        for (int j = 0; j < weighted.Length; j++) total += weighted[j];
        if (!double.IsFinite(total) || total <= 0)
            throw new InvalidDataException("Combined probability sum is invalid.");
        for (int j = 0; j < weighted.Length; j++) probabilities[j] = checked((float)(weighted[j] / total));
    }

    private float[] InferMember(MemberRuntime member, IReadOnlyList<CandleData> candles)
    {
        if (candles.Count < member.Window) throw new InvalidDataException("Insufficient candle history.");
        var tensor = new float[member.FeatureCount];
        int start = candles.Count - member.Window;
        if (member.Scaler is { } scaler)
        {
            var raw = new double[member.FeatureCount];
            PredictionService.BuildFixedRawTensor(candles, start, scaler, member.FeatureSpec, _indicators, raw);
            scaler.Transform(raw, tensor);
        }
        else switch (member.Mode)
        {
            case PredictionFeatureMode.LogReturn:
                _processor.ComputeLogReturns(candles, start, member.Window, tensor); break;
            case PredictionFeatureMode.LogReturnOhlc:
                _processor.ComputeLogReturnsOhlc(candles, start, member.Window, tensor); break;
            case PredictionFeatureMode.ZScoreStandardized:
                _processor.NormalizeZScoreOhlcv(candles, start, member.Window, tensor); break;
            case PredictionFeatureMode.ZScoreOhlcvJoint:
                _processor.ComputeJointZScoreOhlcv(candles, start, member.Window, tensor); break;
            case PredictionFeatureMode.ComposedFeatures:
                PredictionService.BuildComposedTensor(candles, start, member.Window, member.FeatureSpec!, _indicators, tensor); break;
            default:
                _processor.NormalizeCandles(candles, start, member.Window, tensor); break;
        }
        using var inputValue = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, tensor.AsMemory(),
            member.InputShape);
        using var result = member.Session.Run(member.Options, member.InputNames, new[] { inputValue }, member.OutputNames);
        if (result.Count != 1 || !result[0].IsTensor)
            throw new InvalidDataException("Member output must be one tensor.");
        var shape = result[0].GetTensorTypeAndShape();
        var dimensions = shape.Shape;
        int expectedWidth = member.CanonicalIndices.Length;
        if (shape.ElementDataType != TensorElementType.Float || shape.DimensionsCount != 2
            || dimensions.Length != 2 || dimensions[0] != 1 || dimensions[1] != expectedWidth
            || shape.ElementCount != expectedWidth)
            throw new InvalidDataException("Member runtime output must be float32 [1, class count].");
        var values = result[0].GetTensorDataAsSpan<float>();
        if (values.Length != expectedWidth)
            throw new InvalidDataException("Member runtime output element count disagrees with class labels.");
        return values.ToArray();
    }

    public void Dispose()
    {
        _runtimeGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _runtime?.Dispose();
            _runtime = null;
        }
        finally { _runtimeGate.Release(); }
    }

    private sealed class RuntimeBundle(string[] canonical) : IDisposable
    {
        public string[] Canonical { get; } = canonical;
        public List<MemberRuntime> Members { get; } = new();
        public void Dispose()
        {
            foreach (var member in Members) member.Dispose();
            Members.Clear();
        }
    }

    private sealed class MemberRuntime : IDisposable
    {
        public MemberRuntime(ModelGenerationLease lease, InferenceSession session, PredictionFeatureMode mode,
            int window, int channels, int featureCount, FeatureSpec? featureSpec, FixedScaler? scaler, string input, string output,
            int[] canonicalIndices)
        {
            Lease = lease; Session = session; Mode = mode; Window = window; Channels = channels;
            FeatureCount = featureCount; FeatureSpec = featureSpec; Scaler = scaler; CanonicalIndices = canonicalIndices;
            InputNames = new[] { input }; OutputNames = new[] { output };
            InputShape = new long[] { 1, window, channels };
        }
        public ModelGenerationLease Lease { get; }
        public InferenceSession Session { get; }
        public PredictionFeatureMode Mode { get; }
        public int Window { get; }
        public int Channels { get; }
        public int FeatureCount { get; }
        public FeatureSpec? FeatureSpec { get; }
        public FixedScaler? Scaler { get; }
        public int[] CanonicalIndices { get; }
        public string[] InputNames { get; }
        public string[] OutputNames { get; }
        public long[] InputShape { get; }
        public RunOptions Options { get; } = new();
        public void Dispose() { Options.Dispose(); Session.Dispose(); Lease.Dispose(); }
    }
}
