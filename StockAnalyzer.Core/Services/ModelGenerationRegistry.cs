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
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services;

public sealed record ModelSidecar(string File, string Hash, string Kind);

public sealed record ModelGenerationManifest(
    int SchemaVersion, string ModelId, string ModelFile, string ModelHash,
    IReadOnlyList<ModelSidecar> RequiredSidecars, string ContractHash,
    string EvaluationRevision, string TargetType, string Timeframe, int Horizon,
    string ClassOrder, double Score)
{
    public TrainingLineage? TrainingLineage { get; init; }
}

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

    public ModelActivationConfirmationRequiredException(string candidateId, string? expectedActiveId)
        : this()
    {
        CandidateId = candidateId;
        ExpectedActiveId = expectedActiveId;
    }

    public string? CandidateId { get; }
    public string? ExpectedActiveId { get; }
}

public sealed class ModelActivationContextChangedException : InvalidOperationException
{
    public ModelActivationContextChangedException()
        : base("Active model generation changed before confirmation.") { }
}

public interface IModelGenerationRegistry
{
    string? ActiveId { get; }
    string? PreviousId { get; }
    IReadOnlyList<ModelGenerationSummary> List();
    Task<ModelGenerationManifest> RegisterAsync(string onnxPath, string? metricsPath = null, CancellationToken ct = default);
    Task<bool> ActivateAsync(string modelId, bool manual = false, bool confirmLowerScore = false, CancellationToken ct = default);
    Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId, CancellationToken ct = default);
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
    public const int SchemaVersion = 2;
    private const int LegacySchemaVersion = 1;
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
            var report = metricDoc.RootElement;
            var evidence = ValidateEvidence(metricDoc.RootElement, modelHash);
            var (revision, target, timeframe, horizon, classOrder, score) = evidence;

            using var session = new InferenceSession(stagedModel);
            var metadata = session.ModelMetadata.CustomMetadataMap;
            var outputContract = PredictionModelMetadata.ReadOutputContract(metadata);
            var lineage = TrainingLineage.Read(metadata.GetValueOrDefault(TrainingCheckpointContract.LineageKey));
            var metricLineage = report.TryGetProperty("training_lineage", out var lineageElement)
                ? TrainingLineage.Read(lineageElement.GetRawText()) : null;
            if (lineage != metricLineage)
                throw new InvalidDataException("ONNX and evaluation training lineage disagree.");
            if (lineage?.ParentModelId is { } parentId)
            {
                if (!_known.TryGetValue(parentId, out var parent) || !Verify(parent)
                    || parent.ModelHash != lineage.ParentModelSha256)
                    throw new InvalidDataException("Training parent generation is unavailable or mismatched.");
            }
            if (outputContract.HorizonBars != horizon
                || !string.Equals(outputContract.Timeframe.ToString(), timeframe, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ONNX output semantic contract and evaluation horizon/timeframe disagree.");
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
            if (!FeatureContractValid(session))
                throw new InvalidDataException("ONNX feature contract cannot be executed by prediction.");
            var scalerRef = metadata.GetValueOrDefault(PredictionModelMetadata.ScalerReferenceKey) ?? "";
            var sidecars = new List<ModelSidecar>();
            if (PredictionModelMetadata.RequiresFixedScaler(metadata))
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
            if (metadata.ContainsKey(ModelAnalysisContract.ReferenceKey))
            {
                var analysisName = modelName + ".analysis.json";
                if (metadata[ModelAnalysisContract.ReferenceKey] != analysisName)
                    throw new InvalidDataException("Analysis reference must resolve inside its generation.");
                await CopyAsync(onnxPath + ".analysis.json", Path.Combine(stage, analysisName), ct).ConfigureAwait(false);
                _ = ModelAnalysis.Load(stagedModel, metadata, modelHash, report.GetProperty("data_revision").GetString() ?? "");
                sidecars.Add(new ModelSidecar(analysisName, HashFile(Path.Combine(stage, analysisName)), "analysis"));
            }
            sidecars.Sort((a, b) => StringComparer.Ordinal.Compare(a.File, b.File));
            var contractHash = ComputeContractHash(metadata, windowSize, target, horizon, timeframe, classOrder, SchemaVersion);
            var id = ComputeModelId(stagedModel, sidecars, contractHash);
            var manifest = new ModelGenerationManifest(SchemaVersion, id, modelName, modelHash,
                Array.AsReadOnly(sidecars.ToArray()), contractHash, revision, target, timeframe, horizon, classOrder, score)
                { TrainingLineage = lineage };
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
        => ActivateCoreAsync(modelId, manual, confirmLowerScore, expectedActiveId: null, checkExpectedActiveId: false, ct);

    public Task<bool> ConfirmActivationAsync(string modelId, string? expectedActiveId, CancellationToken ct = default)
        => ActivateCoreAsync(modelId, manual: true, confirmLowerScore: true, expectedActiveId,
            checkExpectedActiveId: true, ct);

    private async Task<bool> ActivateCoreAsync(string modelId, bool manual, bool confirmLowerScore,
        string? expectedActiveId, bool checkExpectedActiveId, CancellationToken ct)
    {
        await _mutation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ModelGenerationManifest candidate;
            Pointer next;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (checkExpectedActiveId && _pointer.ActiveId != expectedActiveId)
                    throw new ModelActivationContextChangedException();
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
                        throw new ModelActivationConfirmationRequiredException(modelId, activeId);
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
            : ActivateCoreAsync(prior, manual: true, confirmLowerScore, current,
                checkExpectedActiveId: true, ct);
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
            if (m.SchemaVersion is not (LegacySchemaVersion or SchemaVersion) || !IsHash(m.ModelId) || !SafeName(m.ModelFile)
                || !IsHash(m.ModelHash) || !IsHash(m.ContractHash) || !IsHash(m.EvaluationRevision)
                || m.RequiredSidecars is null || m.RequiredSidecars.Count is < 1 or > 3
                || m.RequiredSidecars.Select(s => s?.Kind).Distinct().Count() != m.RequiredSidecars.Count) return false;
            directory ??= Path.Combine(_root, m.ModelId);
            if (HashFile(Path.Combine(directory, m.ModelFile)) != m.ModelHash) return false;
            foreach (var sidecar in m.RequiredSidecars)
                if (sidecar is null || !SafeName(sidecar.File) || !IsHash(sidecar.Hash)
                    || (sidecar.Kind != "metrics" && sidecar.Kind != "scaler" && sidecar.Kind != "analysis")
                    || (sidecar.Kind == "metrics" && sidecar.File != m.ModelFile + ".metrics.json")
                    || (sidecar.Kind == "scaler" && sidecar.File != m.ModelFile + ".scaler.json")
                    || (sidecar.Kind == "analysis" && sidecar.File != m.ModelFile + ".analysis.json")
                    || HashFile(Path.Combine(directory, sidecar.File)) != sidecar.Hash) return false;
            if (m.RequiredSidecars.Count(s => s.Kind == "metrics" && s.File == m.ModelFile + ".metrics.json") != 1)
                return false;
            var expected = ComputeModelId(Path.Combine(directory, m.ModelFile), m.RequiredSidecars, m.ContractHash);
            if (expected != m.ModelId) return false;
            using var metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, m.ModelFile + ".metrics.json")));
            var report = metrics.RootElement;
            var evidence = ValidateEvidence(report, m.ModelHash);
            if (evidence.Revision != m.EvaluationRevision || evidence.Target != m.TargetType
                || evidence.Timeframe != m.Timeframe || evidence.Horizon != m.Horizon
                || evidence.ClassOrder != m.ClassOrder || evidence.Score != m.Score) return false;
            using var session = new InferenceSession(Path.Combine(directory, m.ModelFile));
            var metadata = session.ModelMetadata.CustomMetadataMap;
            var outputContract = PredictionModelMetadata.ReadOutputContract(metadata);
            var lineage = TrainingLineage.Read(metadata.GetValueOrDefault(TrainingCheckpointContract.LineageKey));
            var metricLineage = report.TryGetProperty("training_lineage", out var lineageElement)
                ? TrainingLineage.Read(lineageElement.GetRawText()) : null;
            if (lineage != m.TrainingLineage || lineage != metricLineage) return false;
            if (outputContract.HorizonBars != m.Horizon
                || !string.Equals(outputContract.Timeframe.ToString(), m.Timeframe, StringComparison.OrdinalIgnoreCase))
                return false;
            var windowSize = int.Parse(metadata[PredictionModelMetadata.WindowSizeKey], System.Globalization.CultureInfo.InvariantCulture);
            if (windowSize <= 0) return false;
            var scalerRef = metadata.GetValueOrDefault(PredictionModelMetadata.ScalerReferenceKey) ?? "";
            if (metadata[PredictionModelMetadata.TargetTypeKey] != m.TargetType
                || metadata[PredictionModelMetadata.HorizonKey] != m.Horizon.ToString(System.Globalization.CultureInfo.InvariantCulture)
                || (m.TargetType == PredictionModelMetadata.TargetTypeClassification
                    && metadata[PredictionModelMetadata.ClassOrderKey] != m.ClassOrder)) return false;
            if (!ShapeValid(session, windowSize, m.TargetType, m.ClassOrder)) return false;
            if (!FeatureContractValid(session)) return false;
            bool requiresScaler = PredictionModelMetadata.RequiresFixedScaler(metadata);
            if ((requiresScaler && (scalerRef != m.ModelFile + ".scaler.json"
                || !m.RequiredSidecars.Any(s => s.Kind == "scaler" && s.File == scalerRef)))
                || (!requiresScaler && m.RequiredSidecars.Any(s => s.Kind == "scaler"))) return false;
            if (requiresScaler)
            {
                var scaler = StockAnalyzer.Core.Models.Training.FixedScaler.Load(Path.Combine(directory, m.ModelFile), metadata);
                if (session.InputMetadata.Values.Single().Dimensions[2] != scaler.Statistics.Length) return false;
            }
            bool requiresAnalysis = metadata.ContainsKey(ModelAnalysisContract.ReferenceKey);
            if (requiresAnalysis != m.RequiredSidecars.Any(s => s.Kind == "analysis")) return false;
            if (requiresAnalysis)
                _ = ModelAnalysis.Load(Path.Combine(directory, m.ModelFile), metadata, m.ModelHash,
                    report.GetProperty("data_revision").GetString() ?? "");
            return ComputeContractHash(metadata, windowSize, m.TargetType, m.Horizon, m.Timeframe, m.ClassOrder, m.SchemaVersion) == m.ContractHash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or JsonException or KeyNotFoundException or OnnxRuntimeException or InvalidOperationException
            or FormatException or OverflowException) { return false; }
    }

    private void ValidateConfiguredPredictionContract(ModelGenerationManifest candidate)
    {
        if (_predictionSettings is null) return;
        if (candidate.TargetType == PredictionModelMetadata.TargetTypeClassification
            && candidate.ClassOrder != string.Join(",", _predictionSettings.PredictionClassLabels))
            throw new InvalidOperationException("Model class labels disagree with current prediction settings.");
        using var session = new InferenceSession(Path.Combine(_root, candidate.ModelId, candidate.ModelFile));
        if (PredictionModelMetadata.RequiresFixedScaler(session.ModelMetadata.CustomMetadataMap))
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

    private sealed record EvaluationEvidence(string Revision, string Target, string Timeframe,
        int Horizon, string ClassOrder, double Score);

    private static EvaluationEvidence ValidateEvidence(JsonElement report, string modelHash)
    {
        string Required(string key) => report.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : throw new InvalidDataException($"Required metric field {key} is absent.");
        if (Required("evaluation_status") != "independent_outer")
            throw new InvalidDataException("Only independent outer evaluation is promotable.");
        var revision = Required("evaluation_revision");
        if (!IsHash(revision) || !string.Equals(Required("scored_model_sha256"), modelHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Evaluation revision or scored model hash is invalid.");
        var target = Required("target_type");
        var timeframe = Required("timeframe");
        var classOrder = Required("class_order");
        if (target is not (PredictionModelMetadata.TargetTypeRegression or PredictionModelMetadata.TargetTypeClassification)
            || (target == PredictionModelMetadata.TargetTypeRegression ? classOrder.Length != 0
                : !PredictionModelMetadata.HasValidClassOrder(classOrder)))
            throw new InvalidDataException("Evaluation target or class order is invalid.");
        if (!Enum.TryParse<TrainingTimeframe>(timeframe, true, out var parsedTimeframe)
            || !Enum.IsDefined(parsedTimeframe)
            || parsedTimeframe.ToString().ToLowerInvariant() != timeframe)
            throw new InvalidDataException("Evaluation timeframe is invalid.");
        if (!report.TryGetProperty("horizon", out var h) || !h.TryGetInt32(out var horizon) || horizon <= 0)
            throw new InvalidDataException("Evaluation horizon is invalid.");
        var scoreKey = target == PredictionModelMetadata.TargetTypeRegression ? "fold_rmse" : "fold_macro_f1";
        if (!report.TryGetProperty(scoreKey, out var scoreValue)
            || !scoreValue.TryGetDouble(out var score) || !ValidScore(score, target)
            || !FinalFoldEvidence(report, scoreKey, target))
            throw new InvalidDataException("Final independent score or outer-fold evidence is invalid.");
        return new EvaluationEvidence(revision, target, timeframe, horizon, classOrder, score);
    }

    private static bool ValidScore(double score, string target) => double.IsFinite(score) && score >= 0
        && (target != PredictionModelMetadata.TargetTypeClassification || score <= 1);

    private static bool PositiveInteger(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var raw)
            && double.IsFinite(raw) && raw > 0 && raw <= int.MaxValue
            && raw == Math.Truncate(raw) && (value = (int)raw) > 0;
    }

    private static bool FinalFoldEvidence(JsonElement report, string scoreKey, string target)
    {
        if (!report.TryGetProperty("fold_is_holdout", out var holdout) || !holdout.TryGetDouble(out var holdoutValue)
            || holdoutValue != 1.0
            || !report.TryGetProperty("fold_n", out var count) || !PositiveInteger(count, out _)
            || !report.TryGetProperty("n_splits", out var splits) || !PositiveInteger(splits, out var total)
            || total < 2 || total > TrainingResourceOverrides.MaximumEvaluationFolds
            || !report.TryGetProperty("fold", out var fold) || !fold.TryGetDouble(out var finalFold)
            || finalFold != total - 1
            || !report.TryGetProperty("outer_folds", out var rows) || rows.ValueKind != JsonValueKind.Array
            || rows.GetArrayLength() != total) return false;
        for (var index = 0; index < total; index++)
        {
            var row = rows[index];
            if (!row.TryGetProperty("fold", out var rowFold) || !rowFold.TryGetDouble(out var rowIndex)
                || rowIndex != index
                || !row.TryGetProperty("evaluation_revision", out var rowRevision)
                || !IsHash(rowRevision.GetString())
                || !row.TryGetProperty(scoreKey, out var rowMetric)
                || !rowMetric.TryGetDouble(out var rowScore) || !ValidScore(rowScore, target)) return false;
        }
        var last = rows[total - 1];
        return last.GetProperty("evaluation_revision").GetString() == report.GetProperty("evaluation_revision").GetString()
            && last.GetProperty("scored_model_sha256").GetString() == report.GetProperty("scored_model_sha256").GetString()
            && last.GetProperty(scoreKey).GetDouble() == report.GetProperty(scoreKey).GetDouble();
    }
    private static string ComputeContractHash(IReadOnlyDictionary<string, string> metadata,
        int windowSize, string target, int horizon, string timeframe, string classOrder, int schemaVersion)
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
        var legacyHash = HashText(contract);
        return schemaVersion == LegacySchemaVersion ? legacyHash
            : HashText("model-generation-contract-v2\n" + legacyHash + "\n" + FixedScaler.ComputeContractHash(metadata));
    }
    private static bool ShapeValid(InferenceSession session, int window, string target, string classOrder)
    {
        if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1) return false;
        var input = session.InputMetadata.Values.Single();
        var output = session.OutputMetadata.Values.Single();
        var width = target == PredictionModelMetadata.TargetTypeRegression
            ? 1 : classOrder.Split(',').Length;
        if (!input.IsTensor || input.Dimensions.Length != 3
            || input.Dimensions[1] != window || input.Dimensions[2] <= 0) return false;
        try
        {
            PredictionService.ValidateModelContract(input, output, window, input.Dimensions[2], width,
                session.InputMetadata.Keys.Single(), session.OutputMetadata.Keys.Single());
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static bool FeatureContractValid(InferenceSession session)
    {
        var metadata = session.ModelMetadata.CustomMetadataMap;
        var mode = PredictionModelMetadata.ParseFeatureMode(
            metadata.GetValueOrDefault(PredictionModelMetadata.FeatureModeKey));
        if (mode is null) return false;

        bool fixedScaler = metadata.GetValueOrDefault("normalization") == "fixed_zscore";
        if (fixedScaler && mode is not (PredictionFeatureMode.OhlcvMinMax or PredictionFeatureMode.ComposedFeatures))
            return false;

        FeatureSpec? spec = null;
        if (mode == PredictionFeatureMode.ComposedFeatures)
        {
            if (!metadata.TryGetValue(PredictionModelMetadata.FeatureSpecKey, out var rawSpec)) return false;
            try
            {
                spec = JsonSerializer.Deserialize<FeatureSpec>(rawSpec, TrainingConfigJson.Options);
                PredictionService.ValidateComposedFeatureSpec(spec, FeatureSpec.MaxChannels, IndicatorFactory.Default);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
            {
                return false;
            }
            if (fixedScaler && spec!.Channels.Any(channel => channel.Normalization != ChannelNormalization.None))
                return false;
        }
        else if (metadata.ContainsKey(PredictionModelMetadata.FeatureSpecKey)) return false;

        int baseChannels = PredictionService.ResolveFeaturesPerBar(mode.Value, spec);
        int[] lags = Array.Empty<int>();
        if (fixedScaler)
        {
            try
            {
                lags = JsonSerializer.Deserialize<int[]>(metadata["lags"]);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
            {
                return false;
            }
            if (!TrainingResourceOverrides.ValidLags(lags, out _)
                || (spec is not null && !spec.Lags!.SequenceEqual(lags))) return false;
        }
        int expectedWidth = checked(baseChannels * (1 + lags.Length));
        if (metadata.TryGetValue("channels", out var rawChannels)
            && (!int.TryParse(rawChannels, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var declaredChannels)
                || declaredChannels != expectedWidth)) return false;
        return session.InputMetadata.Values.Single().Dimensions[2] == expectedWidth;
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
        // Activation owns _mutation through durable pointer save and memory publication.
        // Shutdown becomes effective only after that whole commit has finished.
        _mutation.Wait();
        try
        {
            lock (_gate) _disposed = true;
        }
        finally { _mutation.Release(); }
    }

    internal sealed record Pointer(string? ActiveId, string[] History);
}
