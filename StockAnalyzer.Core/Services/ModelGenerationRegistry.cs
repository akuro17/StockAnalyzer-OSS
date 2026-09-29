using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public sealed record ModelSidecar(string File, string Hash, string Kind);

public sealed record ModelGenerationManifest(
    int SchemaVersion, string ModelId, string ModelFile, string ModelHash,
    IReadOnlyList<ModelSidecar> RequiredSidecars, string ContractHash,
    string EvaluationRevision, string TargetType, string Timeframe, int Horizon,
    string ClassOrder, double Score);

public sealed record ModelGenerationSummary(
    string ModelId, string ModelFile, string TargetType, string Timeframe,
    int Horizon, double Score, string EvaluationRevision, bool IsActive)
{
    public string MetricName => TargetType == PredictionModelMetadata.TargetTypeRegression ? "RMSE" : "macro-F1";
    public string ActiveMarker => IsActive ? "●" : "";
}

public sealed class ModelActivationConfirmationRequiredException : InvalidOperationException
{
    public ModelActivationConfirmationRequiredException()
        : base("Activation would not improve the current score; explicit confirmation is required.") { }
}

public interface IModelGenerationRegistry
{
    string? ActiveId { get; }
    string? PreviousId { get; }
    IReadOnlyList<ModelGenerationSummary> List();
    Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null, CancellationToken ct = default);
    Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false, CancellationToken ct = default);
    Task<bool> RollbackAsync(bool confirmLowerScore = false, CancellationToken ct = default);
    ModelGenerationLease? AcquireActive();
    ModelGenerationLease? Acquire(string modelId) => null;
    ModelGenerationManifest? GetManifest(string modelId) => null;
}

public sealed class ModelGenerationLease : IDisposable
{
    private Action? _release;
    internal ModelGenerationLease(string id, string path, Action release)
    {
        ModelId = id;
        ModelPath = path;
        _release = release;
    }

    public string ModelId { get; }
    public string ModelPath { get; }
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

/// <summary>Content-addressed, verified model generations and an atomic active pointer.</summary>
public sealed class ModelGenerationRegistry : IModelGenerationRegistry, IDisposable
{
    public const int SchemaVersion = 1;
    public const int PriorGenerationLimit = 5;
    private const string ManifestName = "manifest.json";
    private const string PointerName = "active.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _root;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly Dictionary<string, ModelGenerationManifest> _known = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _leases = new(StringComparer.Ordinal);
    private readonly Func<string, Pointer, Task> _savePointer;
    private readonly IStockAnalyzerSettings? _predictionSettings;
    private Pointer _pointer = new(null, Array.Empty<string>());
    private bool _disposed;

    public ModelGenerationRegistry() : this(PathDiscovery.ResolveModelGenerationsDirectory()) { }

    public ModelGenerationRegistry(IStockAnalyzerSettings settings)
        : this(PathDiscovery.ResolveModelGenerationsDirectory(), predictionSettings: settings) { }

    internal ModelGenerationRegistry(string root, Func<string, Pointer, Task>? savePointer = null,
        IStockAnalyzerSettings? predictionSettings = null)
    {
        _root = Path.GetFullPath(root);
        _predictionSettings = predictionSettings;
        _savePointer = savePointer ?? ((path, pointer) => AtomicJsonFile.SaveAsync(path, pointer, JsonOptions));
        Directory.CreateDirectory(_root);
        var pointerPath = Path.Combine(_root, PointerName);
        if (File.Exists(pointerPath))
            _pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(pointerPath), JsonOptions)
                ?? throw new InvalidDataException("Model active pointer is invalid.");
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            if (Path.GetFileName(dir).StartsWith(".stage_", StringComparison.Ordinal)) continue;
            var path = Path.Combine(dir, ManifestName);
            if (!File.Exists(path)) continue;
            try
            {
                var manifest = JsonSerializer.Deserialize<ModelGenerationManifest>(File.ReadAllText(path), JsonOptions);
                if (manifest is not null && manifest.ModelId == Path.GetFileName(dir) && Verify(manifest, dir))
                    _known.Add(manifest.ModelId, manifest);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException) { /* invalid generation stays unavailable */ }
        }
        if (_pointer.ActiveId is { } active && (!_known.ContainsKey(active) || !Verify(_known[active])))
            throw new InvalidDataException("Active model generation is missing or corrupt.");
    }

    public string? ActiveId { get { lock (_gate) return _pointer.ActiveId; } }
    public string? PreviousId { get { lock (_gate) return _pointer.History?.FirstOrDefault(); } }

    public IReadOnlyList<ModelGenerationSummary> List()
    {
        lock (_gate)
            return _known.Values.Select(m => new ModelGenerationSummary(
                m.ModelId, m.ModelFile, m.TargetType, m.Timeframe, m.Horizon,
                m.Score, m.EvaluationRevision, m.ModelId == _pointer.ActiveId))
                .OrderBy(m => m.ModelId, StringComparer.Ordinal).ToArray();
    }

    public async Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(onnxPath) || !File.Exists(onnxPath))
            throw new FileNotFoundException("ONNX generation source is missing.", onnxPath);
        metricsPath ??= onnxPath + ".metrics.json";
        if (!File.Exists(metricsPath))
            throw new FileNotFoundException("Independent evaluation metrics are required.", metricsPath);
        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        string? stage = null;
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            stage = Path.Combine(_root, ".stage_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var modelName = Path.GetFileName(onnxPath);
            if (!modelName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || !SafeName(modelName))
                throw new InvalidDataException("Invalid ONNX filename.");
            var stagedModel = Path.Combine(stage, modelName);
            await CopyAsync(onnxPath, stagedModel, ct).ConfigureAwait(false);
            var modelHash = HashFile(stagedModel);
            var metricName = modelName + ".metrics.json";
            var stagedMetrics = Path.Combine(stage, metricName);
            await CopyAsync(metricsPath, stagedMetrics, ct).ConfigureAwait(false);
            using var metricDoc = JsonDocument.Parse(File.ReadAllText(stagedMetrics));
            var metric = metricDoc.RootElement;
            string Required(string key) => metric.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : throw new InvalidDataException($"Required metric field {key} is absent.");
            if (Required("evaluation_status") != "independent_outer")
                throw new InvalidDataException("Only independent outer evaluation is promotable.");
            var revision = Required("evaluation_revision");
            if (!IsHash(revision) || !string.Equals(Required("scored_model_sha256"), modelHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Evaluation revision or scored model hash is invalid.");
            var target = Required("target_type");
            var timeframe = Required("timeframe");
            var classOrder = Required("class_order");
            if (!Enum.TryParse<StockAnalyzer.Core.Models.Training.TrainingTimeframe>(timeframe, true, out var parsedTimeframe)
                || parsedTimeframe.ToString().ToLowerInvariant() != timeframe)
                throw new InvalidDataException("Evaluation timeframe is invalid.");
            if (!metric.TryGetProperty("horizon", out var h) || !h.TryGetInt32(out var horizon) || horizon <= 0)
                throw new InvalidDataException("Evaluation horizon is invalid.");
            var scoreKey = target == PredictionModelMetadata.TargetTypeRegression ? "fold_rmse" : "fold_macro_f1";
            if (target is not (PredictionModelMetadata.TargetTypeRegression or PredictionModelMetadata.TargetTypeClassification)
                || !metric.TryGetProperty(scoreKey, out var scoreValue)
                || !scoreValue.TryGetDouble(out var score) || !double.IsFinite(score)
                || score < 0 || (target == PredictionModelMetadata.TargetTypeClassification && score > 1))
                throw new InvalidDataException("Final independent score is missing or nonfinite.");
            if (!FinalFoldEvidence(metric))
                throw new InvalidDataException("Final outer fold evidence is missing.");

            using var session = new InferenceSession(stagedModel);
            var metadata = session.ModelMetadata.CustomMetadataMap;
            string Meta(string key) => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new InvalidDataException($"ONNX metadata {key} is required.");
            _ = Meta(PredictionModelMetadata.FeatureModeKey);
            var window = Meta(PredictionModelMetadata.WindowSizeKey);
            if (!int.TryParse(window, out var windowSize) || windowSize <= 0)
                throw new InvalidDataException("ONNX window is invalid.");
            if (Meta(PredictionModelMetadata.TargetTypeKey) != target
                || Meta(PredictionModelMetadata.HorizonKey) != horizon.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || (target == PredictionModelMetadata.TargetTypeClassification
                    && Meta(PredictionModelMetadata.ClassOrderKey) != classOrder))
                throw new InvalidDataException("ONNX metadata and evaluation contract disagree.");
            if (!ShapeValid(session, windowSize, target, classOrder))
                throw new InvalidDataException("ONNX tensor shape disagrees with the evaluation contract.");
            var scalerRef = metadata.GetValueOrDefault(PredictionModelMetadata.ScalerReferenceKey) ?? "";
            var sidecars = new List<ModelSidecar>();
            if (scalerRef.Length > 0)
            {
                var scalerName = modelName + ".scaler.json";
                if (scalerRef != scalerName || !SafeName(scalerRef))
                    throw new InvalidDataException("Scaler reference must resolve inside its generation.");
                var scalerSource = onnxPath + ".scaler.json";
                if (!File.Exists(scalerSource)) throw new FileNotFoundException("Required scaler sidecar is missing.", scalerSource);
                await CopyAsync(scalerSource, Path.Combine(stage, scalerName), ct).ConfigureAwait(false);
                using var _ = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, scalerName)));
                sidecars.Add(new ModelSidecar(scalerName, HashFile(Path.Combine(stage, scalerName)), "scaler"));
            }
            sidecars.Add(new ModelSidecar(metricName, HashFile(stagedMetrics), "metrics"));
            sidecars.Sort((a, b) => StringComparer.Ordinal.Compare(a.File, b.File));
            var contractHash = ComputeContractHash(metadata, windowSize, target, horizon, timeframe, classOrder);
            var id = ComputeModelId(stagedModel, sidecars, contractHash);
            var manifest = new ModelGenerationManifest(SchemaVersion, id, modelName, modelHash,
                Array.AsReadOnly(sidecars.ToArray()), contractHash, revision, target, timeframe, horizon, classOrder, score);
            File.WriteAllText(Path.Combine(stage, ManifestName), JsonSerializer.Serialize(manifest, JsonOptions));
            if (!Verify(manifest, stage)) throw new InvalidDataException("Staged generation verification failed.");
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                ct.ThrowIfCancellationRequested();
                var destination = Path.Combine(_root, id);
                if (!Directory.Exists(destination)) Directory.Move(stage, destination);
                else
                {
                    if (!Verify(manifest, destination))
                        throw new InvalidDataException("An existing generation with this ID is corrupt.");
                    Directory.Delete(stage, recursive: true);
                }
                _known[id] = manifest;
            }
            stage = null;
            return manifest;
        }
        finally
        {
            if (stage is not null && Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            _mutation.Release();
        }
    }

    public Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false, CancellationToken ct = default)
        => ActivateCoreAsync(modelId, manual, confirmLowerScore, expectedActiveId: null, ct);

    private async Task<bool> ActivateCoreAsync(string modelId, bool manual, bool confirmLowerScore,
        string? expectedActiveId, CancellationToken ct)
    {
        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ModelGenerationManifest candidate;
            Pointer next;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (expectedActiveId is not null && _pointer.ActiveId != expectedActiveId)
                    throw new InvalidOperationException("Active generation changed during rollback.");
                if (!_known.TryGetValue(modelId, out candidate!)) throw new KeyNotFoundException("Unknown model generation.");
                if (_pointer.ActiveId == modelId) return false;
                if (!Verify(candidate)) throw new InvalidDataException("Candidate generation is corrupt.");
                ValidateConfiguredPredictionContract(candidate);
                if (_pointer.ActiveId is { } activeId)
                {
                    var current = _known[activeId];
                    if (!Verify(current)) throw new InvalidDataException("Active generation is corrupt.");
                    if (candidate.EvaluationRevision != current.EvaluationRevision
                        || candidate.TargetType != current.TargetType || candidate.Timeframe != current.Timeframe
                        || candidate.Horizon != current.Horizon || candidate.ClassOrder != current.ClassOrder)
                        throw new InvalidOperationException("Generations require compatible target, horizon, timeframe, labels and evaluation revision.");
                    bool improves = candidate.TargetType == PredictionModelMetadata.TargetTypeRegression
                        ? candidate.Score < current.Score : candidate.Score > current.Score;
                    if (!manual && !improves) return false;
                    if (manual && !improves && !confirmLowerScore)
                        throw new ModelActivationConfirmationRequiredException();
                }
                var history = new List<string>(_pointer.History ?? Array.Empty<string>());
                if (_pointer.ActiveId is { } previous)
                {
                    history.Remove(previous);
                    history.Insert(0, previous);
                }
                history.Remove(modelId);
                next = new Pointer(modelId, history.Take(PriorGenerationLimit).ToArray());
            }
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            await _savePointer(Path.Combine(_root, PointerName), next).ConfigureAwait(false);
            lock (_gate)
            {
                _pointer = next;
            }
            return true;
        }
        finally { _mutation.Release(); }
    }

    public Task<bool> RollbackAsync(bool confirmLowerScore = false, CancellationToken ct = default)
    {
        string? prior;
        string? current;
        lock (_gate)
        {
            prior = _pointer.History?.FirstOrDefault();
            current = _pointer.ActiveId;
        }
        return prior is null ? Task.FromResult(false)
            : ActivateCoreAsync(prior, manual: true, confirmLowerScore, current, ct);
    }

    public ModelGenerationLease? AcquireActive()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pointer.ActiveId is not { } id) return null;
            var manifest = _known[id];
            if (!Verify(manifest)) throw new InvalidDataException("Active generation is corrupt.");
            _leases[id] = _leases.GetValueOrDefault(id) + 1;
            return new ModelGenerationLease(id, Path.Combine(_root, id, manifest.ModelFile), () =>
            {
                lock (_gate) _leases[id]--;
            });
        }
    }

    public ModelGenerationManifest? GetManifest(string modelId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _known.GetValueOrDefault(modelId);
        }
    }

    public ModelGenerationLease? Acquire(string modelId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_known.TryGetValue(modelId, out var manifest)) return null;
            if (!Verify(manifest)) throw new InvalidDataException($"Generation '{modelId}' is corrupt.");
            _leases[modelId] = _leases.GetValueOrDefault(modelId) + 1;
            return new ModelGenerationLease(modelId, Path.Combine(_root, modelId, manifest.ModelFile), () =>
            {
                lock (_gate) _leases[modelId]--;
            });
        }
    }

    private bool Verify(ModelGenerationManifest m, string? directory = null)
    {
        try
        {
            if (m.SchemaVersion != SchemaVersion || !IsHash(m.ModelId) || !SafeName(m.ModelFile)
                || !IsHash(m.ModelHash) || !IsHash(m.ContractHash) || !IsHash(m.EvaluationRevision)
                || m.RequiredSidecars is null || m.RequiredSidecars.Count is < 1 or > 2) return false;
            directory ??= Path.Combine(_root, m.ModelId);
            if (HashFile(Path.Combine(directory, m.ModelFile)) != m.ModelHash) return false;
            foreach (var sidecar in m.RequiredSidecars)
                if (sidecar is null || !SafeName(sidecar.File) || !IsHash(sidecar.Hash)
                    || (sidecar.Kind != "metrics" && sidecar.Kind != "scaler")
                    || (sidecar.Kind == "metrics" && sidecar.File != m.ModelFile + ".metrics.json")
                    || (sidecar.Kind == "scaler" && sidecar.File != m.ModelFile + ".scaler.json")
                    || HashFile(Path.Combine(directory, sidecar.File)) != sidecar.Hash) return false;
            if (m.RequiredSidecars.Count(s => s.Kind == "metrics" && s.File == m.ModelFile + ".metrics.json") != 1)
                return false;
            var expected = ComputeModelId(Path.Combine(directory, m.ModelFile), m.RequiredSidecars, m.ContractHash);
            if (expected != m.ModelId) return false;
            using var metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, m.ModelFile + ".metrics.json")));
            var report = metrics.RootElement;
            if (report.GetProperty("evaluation_status").GetString() != "independent_outer"
                || report.GetProperty("evaluation_revision").GetString() != m.EvaluationRevision
                || !string.Equals(report.GetProperty("scored_model_sha256").GetString(), m.ModelHash, StringComparison.OrdinalIgnoreCase)
                || report.GetProperty("target_type").GetString() != m.TargetType
                || report.GetProperty("timeframe").GetString() != m.Timeframe
                || report.GetProperty("horizon").GetInt32() != m.Horizon
                || report.GetProperty("class_order").GetString() != m.ClassOrder
                || report.GetProperty(m.TargetType == PredictionModelMetadata.TargetTypeRegression
                    ? "fold_rmse" : "fold_macro_f1").GetDouble() != m.Score
                || !FinalFoldEvidence(report)) return false;
            using var session = new InferenceSession(Path.Combine(directory, m.ModelFile));
            var metadata = session.ModelMetadata.CustomMetadataMap;
            var windowSize = int.Parse(metadata[PredictionModelMetadata.WindowSizeKey], System.Globalization.CultureInfo.InvariantCulture);
            var scalerRef = metadata.GetValueOrDefault(PredictionModelMetadata.ScalerReferenceKey) ?? "";
            if (metadata[PredictionModelMetadata.TargetTypeKey] != m.TargetType
                || metadata[PredictionModelMetadata.HorizonKey] != m.Horizon.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || (m.TargetType == PredictionModelMetadata.TargetTypeClassification
                    && metadata[PredictionModelMetadata.ClassOrderKey] != m.ClassOrder)) return false;
            if (!ShapeValid(session, windowSize, m.TargetType, m.ClassOrder)) return false;
            if ((scalerRef.Length > 0 && (scalerRef != m.ModelFile + ".scaler.json"
                || !m.RequiredSidecars.Any(s => s.Kind == "scaler" && s.File == scalerRef)))
                || (scalerRef.Length == 0 && m.RequiredSidecars.Any(s => s.Kind == "scaler"))) return false;
            if (scalerRef.Length > 0)
            {
                var scaler = StockAnalyzer.Core.Models.Training.FixedScaler.Load(Path.Combine(directory, m.ModelFile), metadata);
                if (session.InputMetadata.Values.Single().Dimensions[2] != scaler.Statistics.Length) return false;
            }
            return ComputeContractHash(metadata, windowSize, m.TargetType, m.Horizon, m.Timeframe, m.ClassOrder) == m.ContractHash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or JsonException or KeyNotFoundException or OnnxRuntimeException or InvalidOperationException
            or FormatException or OverflowException) { return false; }
    }

    private void ValidateConfiguredPredictionContract(ModelGenerationManifest candidate)
    {
        if (_predictionSettings is null) return;
        var target = _predictionSettings.PredictionTargetType == TargetType.Regression
            ? PredictionModelMetadata.TargetTypeRegression : PredictionModelMetadata.TargetTypeClassification;
        if (candidate.TargetType != target || (target == PredictionModelMetadata.TargetTypeClassification
            && candidate.ClassOrder != string.Join(",", _predictionSettings.PredictionClassLabels)))
            throw new InvalidOperationException("Model target or class labels disagree with current prediction settings.");
        using var session = new InferenceSession(Path.Combine(_root, candidate.ModelId, candidate.ModelFile));
        if (session.ModelMetadata.CustomMetadataMap.ContainsKey(PredictionModelMetadata.ScalerReferenceKey))
            StockAnalyzer.Core.Models.Training.FixedScaler.Load(
                Path.Combine(_root, candidate.ModelId, candidate.ModelFile), session.ModelMetadata.CustomMetadataMap);
        string? featureSpec = _predictionSettings.PredictionFeatureMode == PredictionFeatureMode.ComposedFeatures
            ? JsonSerializer.Serialize(_predictionSettings.PredictionFeatureSpec,
                StockAnalyzer.Core.Models.Training.TrainingConfigJson.Options) : null;
        PredictionModelMetadata.Validate(session.ModelMetadata.CustomMetadataMap,
            _predictionSettings.PredictionFeatureMode, _predictionSettings.PredictionWindowSize,
            _predictionSettings.PredictionClassLabels, NullLogger.Instance, featureSpec);
    }

    private static bool SafeName(string? name) => !string.IsNullOrEmpty(name) && name == Path.GetFileName(name)
        && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\\')
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool FinalFoldEvidence(JsonElement report)
    {
        if (!report.TryGetProperty("fold_is_holdout", out var holdout) || holdout.GetDouble() != 1.0
            || !report.TryGetProperty("fold_n", out var count) || !count.TryGetDouble(out var n) || n <= 0
            || !report.TryGetProperty("n_splits", out var splits) || !splits.TryGetDouble(out var totalRaw)
            || !double.IsFinite(totalRaw) || totalRaw != Math.Truncate(totalRaw)
            || totalRaw < 2 || totalRaw > StockAnalyzer.Core.Models.Training.TrainingResourceOverrides.MaximumEvaluationFolds
            || !report.TryGetProperty("fold", out var fold) || fold.GetDouble() != totalRaw - 1
            || !report.TryGetProperty("outer_folds", out var rows) || rows.ValueKind != JsonValueKind.Array
            || rows.GetArrayLength() != (int)totalRaw) return false;
        for (var index = 0; index < (int)totalRaw; index++)
        {
            var row = rows[index];
            if (row.GetProperty("fold").GetDouble() != index
                || !IsHash(row.GetProperty("evaluation_revision").GetString() ?? "")) return false;
        }
        var last = rows[(int)totalRaw - 1];
        var scoreKey = report.GetProperty("target_type").GetString() == PredictionModelMetadata.TargetTypeRegression
            ? "fold_rmse" : "fold_macro_f1";
        return last.GetProperty("evaluation_revision").GetString() == report.GetProperty("evaluation_revision").GetString()
            && last.GetProperty("scored_model_sha256").GetString() == report.GetProperty("scored_model_sha256").GetString()
            && last.GetProperty(scoreKey).GetDouble() == report.GetProperty(scoreKey).GetDouble();
    }
    private static string ComputeContractHash(IReadOnlyDictionary<string, string> metadata,
        int windowSize, string target, int horizon, string timeframe, string classOrder)
    {
        var contract = JsonSerializer.Serialize(new
        {
            featureMode = metadata[PredictionModelMetadata.FeatureModeKey],
            windowSize,
            channels = metadata.GetValueOrDefault("channels") ?? "",
            channelOrder = metadata.GetValueOrDefault("channel_order") ?? "",
            featureSpec = metadata.GetValueOrDefault(PredictionModelMetadata.FeatureSpecKey) ?? "",
            neutralThreshold = metadata.GetValueOrDefault("neutral_threshold") ?? "",
            normalization = metadata.GetValueOrDefault("normalization") ?? "",
            priceAdjustment = metadata.GetValueOrDefault("price_adjustment") ?? "",
            scalerRef = metadata.GetValueOrDefault(PredictionModelMetadata.ScalerReferenceKey) ?? "",
            target, horizon, timeframe, classOrder,
        });
        return HashText(contract);
    }
    private static bool ShapeValid(InferenceSession session, int window, string target, string classOrder)
    {
        if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1) return false;
        var input = session.InputMetadata.Values.Single();
        var output = session.OutputMetadata.Values.Single();
        var width = target == PredictionModelMetadata.TargetTypeRegression
            ? 1 : classOrder.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
        return input.IsTensor && output.IsTensor
            && input.Dimensions.Length == 3 && output.Dimensions.Length == 2
            && input.Dimensions[1] == window && input.Dimensions[2] > 0
            && (output.Dimensions[1] == width || output.Dimensions[1] == -1);
    }
    private static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string ComputeModelId(string modelPath, IEnumerable<ModelSidecar> sidecars, string contractHash)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var model = File.OpenRead(modelPath))
        {
            var buffer = new byte[1024 * 1024];
            int count;
            while ((count = model.Read(buffer, 0, buffer.Length)) > 0)
                digest.AppendData(buffer, 0, count);
        }
        foreach (var sidecar in sidecars.OrderBy(s => s.File, StringComparer.Ordinal))
            digest.AppendData(Convert.FromHexString(sidecar.Hash));
        digest.AppendData(Convert.FromHexString(contractHash));
        return Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static async Task CopyAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = File.OpenRead(source);
        await using var output = File.Create(destination);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _mutation.Wait();
        _mutation.Release();
    }

    internal sealed record Pointer(string? ActiveId, string[] History);
}
