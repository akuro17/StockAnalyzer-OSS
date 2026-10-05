using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;
using StockAnalyzer.Core.Services.ReinforcementLearning;

namespace StockAnalyzer.Core.Services;

public sealed partial class ModelGenerationRegistry
{
    private readonly Dictionary<string, RlGeneration> _rlKnown = new(StringComparer.Ordinal);
    private Pointer _rlPointer = new(null, Array.Empty<string>());
    private Pointer _rlContinuousPointer = new(null, Array.Empty<string>());
    private bool _rlLoaded;
    internal Action<string>? RlStagingPrepared { get; init; }
    private string? _rlPointerError, _rlContinuousPointerError;
    private string RlRoot => Path.Combine(_root, RlFiles.RlFolder);
    private static string[] RlRequiredFiles => RequiredRlFiles(false);
    private static string[] RequiredRlFiles(bool continuous, bool temporal = false) => new[]
    {
        RlFiles.Model, RlFiles.Model + RlFiles.ScalerSuffix, RlFiles.Model + RlFiles.EvaluationSuffix,
        RlFiles.Native, RlFiles.Job, RlFiles.Dependencies, RlFiles.Parity, RlFiles.SourceManifest,
        RlFiles.Bars, RlFiles.Actions, RlFiles.RawParquet, RlFiles.MetadataParquet, RlFiles.Catalog, RlFiles.BaselineActions,
    }.Concat(continuous ? new[] { RlContinuousContract.DirectionModel, RlContinuousContract.DirectionScaler, RlContinuousContract.DirectionManifest }
        : Array.Empty<string>()).Concat(temporal ? new[] { continuous ? RlTemporalContract.DirectionEvidenceFile : RlTemporalContract.EvidenceFile }
        : Array.Empty<string>()).ToArray();

    public string? RlActiveId { get { lock (_gate) { using var transaction = EnterStore(); EnsureRlLoaded(); return _rlPointerError is null ? _rlPointer.ActiveId : null; } } }
    public string? RlContinuousActiveId { get { lock (_gate) { using var transaction = EnterStore(); EnsureRlLoaded(); return _rlContinuousPointerError is null ? _rlContinuousPointer.ActiveId : null; } } }

    private void EnsureRlLoaded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_rlLoaded) return;
        RequireStorePath(RlRoot);
        Directory.CreateDirectory(RlRoot);
        _rlKnown.Clear();
        foreach (var directory in Directory.EnumerateDirectories(RlRoot))
        {
            if (!IsHash(Path.GetFileName(directory))) continue;
            try
            {
                string manifestPath = Path.Combine(directory, RlFiles.Manifest);
                RequireStorePath(manifestPath);
                if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > Coordination.MetadataByteLimit) continue;
                RequireStoreTree(directory);
                var generation = JsonSerializer.Deserialize<RlGeneration>(File.ReadAllText(manifestPath), RlFiles.Json);
                if (generation is not null && generation.ModelId == Path.GetFileName(directory) && VerifyRl(generation, directory, verifyDependency: false))
                    _rlKnown.Add(generation.ModelId, generation);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException) { /* Unverified generations remain unavailable. */ }
        }
        foreach (var generation in _rlKnown.Values.Where(g => g.Target == RlContinuousContract.Target && g.TemporalSpecificationId is not null).ToArray())
            if (!VerifyRlDependency(generation)) _rlKnown.Remove(generation.ModelId);
        (_rlPointer, _rlPointerError) = LoadRlPointer(RlFiles.Target);
        (_rlContinuousPointer, _rlContinuousPointerError) = LoadRlPointer(RlContinuousContract.Target);
        _rlLoaded = true;
    }

    public IReadOnlyList<RlGeneration> ListRl()
    {
        lock (_gate)
        {
            using var transaction = EnterStore();
            EnsureRlLoaded();
            return _rlKnown.Values.Where(g => VerifyRlBytes(g)).OrderByDescending(g => g.CreatedUtc)
                .ThenBy(g => g.ModelId, StringComparer.Ordinal).Select(CloneRl).ToArray();
        }
    }

    public RlGeneration? GetRlGeneration(string modelId)
    {
        lock (_gate)
        {
            using var transaction = EnterStore();
            EnsureRlLoaded();
            return _rlKnown.TryGetValue(modelId, out var value) ? CloneRl(value) : null;
        }
    }

    public RlTemporalEvidence? CaptureRlTemporalEvidence(string modelId)
    {
        using var lease = AcquireRl(modelId);
        if (lease is null) return null;
        lock (_gate)
        {
            var generation = _rlKnown[modelId];
            if (generation.Target != RlFiles.Target || !VerifyRl(generation))
                throw new RlTemporalException(RlTemporalRejection.IdentityMismatch, "Frozen direction generation is not a valid T15 producer.");
            string directory = Path.GetDirectoryName(lease.ModelPath)!;
            using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
            var limits = job.RootElement.GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)!;
            return RlTemporalValidator.LoadAndValidateProducer(directory, RlSourceSnapshot.Load(directory, limits));
        }
    }

    public async Task<RlGeneration> RegisterRlAsync(string artifactDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        artifactDirectory = Path.GetFullPath(artifactDirectory);
        await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        string? stage = null;
        try
        {
            using var transaction = EnterStore(cancellationToken);
            lock (_gate) EnsureRlLoaded();
            stage = Path.Combine(RlRoot, ".stage_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            using var candidateJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(artifactDirectory, RlFiles.Job)));
            bool continuous = candidateJob.RootElement.TryGetProperty("continuous_defaults", out _);
            bool temporal = RlTemporalValidator.IsVersioned(candidateJob.RootElement);
            if (continuous && !temporal)
                throw new RlTemporalException(RlTemporalRejection.MissingEvidence,
                    "New T16 registration requires temporal qualification. Existing legacy generations remain readable research artifacts.");
            foreach (string file in RequiredRlFiles(continuous, temporal))
            {
                string source = Path.Combine(artifactDirectory, file);
                if (!File.Exists(source)) throw new FileNotFoundException($"Required RL artifact {file} is missing.", source);
                string stagedFile = Path.Combine(stage, file);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedFile)!);
                await CopyAsync(source, stagedFile, cancellationToken).ConfigureAwait(false);
            }
            RlStagingPrepared?.Invoke(stage);
            RlGeneration generation = ValidateRlCandidate(stage);
            if (continuous)
            {
                lock (_gate)
                    if (!_rlKnown.TryGetValue(generation.FrozenDirectionModelId!, out var frozen) || frozen.Target != RlFiles.Target || !VerifyRlBytes(frozen)
                        || frozen.Files[RlFiles.Model] != generation.Files[RlContinuousContract.DirectionModel]
                        || frozen.Files[RlFiles.Model + RlFiles.ScalerSuffix] != generation.Files[RlContinuousContract.DirectionScaler])
                        throw new InvalidDataException("Continuous generation requires its exact qualified T15 dependency in this registry.");
                using var directionLease = AcquireRl(generation.FrozenDirectionModelId!)
                    ?? throw new RlTemporalException(RlTemporalRejection.MissingEvidence, "Frozen dependency generation is unavailable.");
                var reconstructed = CaptureRlTemporalEvidence(generation.FrozenDirectionModelId!)
                    ?? throw new RlTemporalException(RlTemporalRejection.MissingEvidence, "Frozen dependency temporal proof is unavailable.");
                using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(stage, RlFiles.Job)));
                var limits = job.RootElement.GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)!;
                var direction = job.RootElement.GetProperty("direction").Deserialize<RlDirectionDependency>(RlFiles.Json)!;
                RlTemporalValidator.ValidateDependency(stage, job.RootElement, direction, RlSourceSnapshot.Load(stage, limits), reconstructed);
            }
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Path.Combine(RlRoot, generation.ModelId);
            await AtomicJsonFile.SaveAsync(Path.Combine(stage, RlFiles.Manifest), generation, RlFiles.Json).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (Directory.Exists(destination))
                {
                    var existing = JsonSerializer.Deserialize<RlGeneration>(File.ReadAllText(Path.Combine(destination, RlFiles.Manifest)), RlFiles.Json);
                    if (existing is null || existing.ModelId != generation.ModelId || !VerifyRl(existing, destination))
                        throw new InvalidDataException("Existing RL generation is corrupt; registration cannot overwrite it.");
                    generation = existing;
                }
                else
                {
                    Directory.Move(stage, destination);
                    stage = null;
                }
                _rlKnown[generation.ModelId] = generation;
            }
            // Registration never changes either active pointer. Activation is a separate explicit user operation.
            return CloneRl(generation);
        }
        catch (Exception error) { RecordStoreFailure("rl-register", error); throw; }
        finally
        {
            try { if (stage is not null && Directory.Exists(stage)) DeleteRlDirectory(stage); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Cleanup never wedges the common mutation owner. */ }
            finally { _mutation.Release(); }
        }
    }

    public async Task<bool> ActivateRlAsync(string modelId, CancellationToken cancellationToken = default)
    {
        await EnterMutationAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var transaction = EnterStore(cancellationToken);
            Pointer next;
            bool continuous;
            lock (_gate)
            {
                EnsureRlLoaded();
                if (!_rlKnown.TryGetValue(modelId, out var candidate)) throw new KeyNotFoundException("Unknown RL generation.");
                if (!VerifyRl(candidate)) throw new InvalidDataException("RL generation is corrupt.");
                continuous = candidate.Target == RlContinuousContract.Target;
                if ((continuous ? _rlContinuousPointerError : _rlPointerError) is not null)
                    throw new InvalidDataException("Unavailable: explicit verified-backup recovery is required before activation.");
                var pointer = continuous ? _rlContinuousPointer : _rlPointer;
                if (pointer.ActiveId == modelId) return false;
                var history = new List<string>(pointer.History);
                history.Remove(modelId);
                if (pointer.ActiveId is { } old)
                {
                    history.Remove(old);
                    history.Insert(0, old);
                }
                next = new Pointer(modelId, history.Take(RlContract.RollbackGenerationCount).ToArray());
            }
            cancellationToken.ThrowIfCancellationRequested();
            string pointerPath = Path.Combine(RlRoot, continuous ? RlContinuousContract.ActiveFile : RlFiles.Active);
            RequirePointerWritePaths(pointerPath);
            await _savePointer(pointerPath, next).ConfigureAwait(false);
            // Dispose shares this mutation lock; durable commit precedes memory publication, matching supervised ownership.
            lock (_gate)
            {
                if (continuous) _rlContinuousPointer = next; else _rlPointer = next;
                PruneRl(continuous ? RlContinuousContract.Target : RlFiles.Target);
            }
            return true;
        }
        catch (Exception error) { RecordStoreFailure("rl-activate", error); throw; }
        finally { _mutation.Release(); }
    }

    public ModelGenerationLease? AcquireRlActive()
    {
        lock (_gate)
        {
            using var transaction = EnterStore();
            EnsureRlLoaded();
            if (_rlPointerError is not null) return null;
            return _rlPointer.ActiveId is { } id ? AcquireRl(id) : null;
        }
    }

    public ModelGenerationLease? AcquireRl(string modelId)
    {
        lock (_gate)
        {
            using var transaction = EnterStore();
            EnsureRlLoaded();
            if (!_rlKnown.TryGetValue(modelId, out var generation)) return null;
            if (!VerifyRlBytes(generation)) throw new InvalidDataException("RL generation files are corrupt.");
            if (generation.Target == RlContinuousContract.Target && generation.TemporalSpecificationId is not null && !VerifyRlDependency(generation))
                throw new RlTemporalException(RlTemporalRejection.IdentityMismatch, "Frozen dependency temporal qualification is inconsistent.");
            string key = RlFiles.Target + ":" + modelId;
            var storeLease = CreateStoreLease(Coordination.RlLease, modelId);
            _leases[key] = _leases.GetValueOrDefault(key) + 1;
            return new ModelGenerationLease(modelId, Path.Combine(RlRoot, modelId, RlFiles.Model), () =>
            {
                storeLease.Dispose(); lock (_gate) _leases[key]--;
            });
        }
    }

    private static RlGeneration CloneRl(RlGeneration value)
        => value with { Files = new Dictionary<string, string>(value.Files, StringComparer.Ordinal) };

    private bool VerifyRlBytes(RlGeneration generation, string? directory = null)
    {
        try
        {
            directory ??= Path.Combine(RlRoot, generation.ModelId);
            bool continuous = generation.Target == RlContinuousContract.Target;
            var requiredFiles = RequiredRlFiles(continuous, generation.TemporalSpecificationId is not null);
            if (!IsHash(generation.ModelId) || generation.Target is not (RlFiles.Target or RlContinuousContract.Target)
                || generation.ContractHash != (continuous ? RlContinuousContract.ContractHash : RlContract.ContractHash)
                || !IsHash(generation.SourceRevision) || !IsHash(generation.EvaluationRevision)
                || generation.Files is null || generation.Files.Count != requiredFiles.Length
                || string.IsNullOrWhiteSpace(generation.Symbol)) return false;
            if (generation.TemporalSpecificationId is not null && generation.TemporalSpecificationId != RlTemporalContract.SpecificationId) return false;
            foreach (string name in requiredFiles)
                if (!generation.Files.TryGetValue(name, out string? hash) || !IsHash(hash)
                    || ContentHash.OfFile(Path.Combine(directory, name)) != hash) return false;
            return ComputeRlId(directory, generation.Files, continuous) == generation.ModelId;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    private bool VerifyRl(RlGeneration generation, string? directory = null, bool verifyDependency = true)
    {
        directory ??= Path.Combine(RlRoot, generation.ModelId);
        if (!VerifyRlBytes(generation, directory)) return false;
        try
        {
            RlGeneration actual = ValidateRlCandidate(directory);
            return actual.ModelId == generation.ModelId && actual.Symbol == generation.Symbol
                && actual.SourceRevision == generation.SourceRevision && actual.EvaluationRevision == generation.EvaluationRevision
                && actual.ValidationReturn == generation.ValidationReturn && actual.OosReturn == generation.OosReturn
                && actual.Target == generation.Target && actual.FrozenDirectionModelId == generation.FrozenDirectionModelId
                && actual.TemporalSpecificationId == generation.TemporalSpecificationId && actual.TemporalStatus == generation.TemporalStatus
                && (!verifyDependency || generation.Target != RlContinuousContract.Target || generation.TemporalSpecificationId is null || VerifyRlDependency(generation));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or OnnxRuntimeException) { return false; }
    }

    private bool VerifyRlDependency(RlGeneration generation)
    {
        try
        {
            if (!_rlKnown.TryGetValue(generation.FrozenDirectionModelId!, out var frozen) || frozen.Target != RlFiles.Target || !VerifyRl(frozen)) return false;
            string frozenDirectory = Path.Combine(RlRoot, frozen.ModelId), directory = Path.Combine(RlRoot, generation.ModelId);
            using var frozenJob = JsonDocument.Parse(File.ReadAllText(Path.Combine(frozenDirectory, RlFiles.Job)));
            var limits = frozenJob.RootElement.GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)!;
            var evidence = RlTemporalValidator.LoadAndValidateProducer(frozenDirectory, RlSourceSnapshot.Load(frozenDirectory, limits));
            using var job = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
            limits = job.RootElement.GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)!;
            var direction = job.RootElement.GetProperty("direction").Deserialize<RlDirectionDependency>(RlFiles.Json)!;
            if (direction.ModelId != frozen.ModelId || direction.ModelSha256 != frozen.Files[RlFiles.Model]
                || direction.ScalerSha256 != frozen.Files[RlFiles.Model + RlFiles.ScalerSuffix]) return false;
            RlTemporalValidator.ValidateDependency(directory, job.RootElement, direction, RlSourceSnapshot.Load(directory, limits), evidence);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    private static string ComputeRlId(string directory, IReadOnlyDictionary<string, string> files, bool continuous = false)
        => ComputeModelId(Path.Combine(directory, RlFiles.Model), files.Where(p => p.Key != RlFiles.Model)
            .Select(p => new ModelSidecar(p.Key, p.Value, "rl")), continuous ? RlContinuousContract.ContractHash : RlContract.ContractHash);

    private static RlGeneration ValidateRlCandidate(string directory)
    {
        try { return ValidateRlCandidateCore(directory); }
        catch (Exception ex) when (ex is KeyNotFoundException or JsonException or ArgumentException or InvalidOperationException)
        { throw new InvalidDataException("RL candidate evidence or runtime contract is invalid.", ex); }
    }

    private static RlGeneration ValidateRlCandidateCore(string directory)
    {
        using var jobDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Job)));
        var job = jobDocument.RootElement;
        bool continuous = job.TryGetProperty("continuous_defaults", out var continuousDefaults);
        bool temporal = RlTemporalValidator.IsVersioned(job);
        string contractHash = continuous ? RlContinuousContract.ContractHash : RlContract.ContractHash;
        var limits = job.GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)
            ?? throw new InvalidDataException("Captured RL resource limits are required.");
        limits.Validate();
        var costs = job.GetProperty("costs").Deserialize<BacktestConfiguration>(RlFiles.Json)
            ?? throw new InvalidDataException("Captured RL costs are required.");
        costs.Validate();
        if (costs.ExecutionModel != ExecutionModel.YFinanceApproximate || costs.SizingModel != PositionSizingModel.FixedQuantity
            || costs.SizingParameter != RlContract.FixedQuantity || costs.InitialMarginRatio != RlContract.InitialMarginRatio
            || costs.MaintenanceMarginRatio != BacktestDefaults.MaintenanceMarginRatio)
            throw new InvalidDataException("Saved RL account configuration is incompatible.");
        if (job.TryGetProperty("smoke_only", out var smoke) && smoke.GetBoolean())
            throw new InvalidDataException("Smoke artifacts cannot become RL generations.");
        JsonElement defaults = job.GetProperty("defaults");
        foreach (var expected in RlContract.ToWireDefaults())
        {
            if (!defaults.TryGetProperty(expected.Key, out var value)) throw new InvalidDataException($"Saved RL default {expected.Key} is missing.");
            var canonical = JsonSerializer.SerializeToElement(expected.Value);
            bool equal = canonical.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number
                && canonical.TryGetDecimal(out decimal a) && value.TryGetDecimal(out decimal b)
                ? a == b : canonical.GetRawText() == value.GetRawText();
            if (!equal) throw new InvalidDataException($"Saved RL default {expected.Key} differs from the adopted contract.");
        }
        if (continuous)
            foreach (var expected in RlContinuousContract.ToWireDefaults())
            {
                if (!continuousDefaults.TryGetProperty(expected.Key, out var value)) throw new InvalidDataException("Saved continuous default missing.");
                var canonical = JsonSerializer.SerializeToElement(expected.Value);
                bool equal = canonical.ValueKind == JsonValueKind.Number && value.ValueKind == JsonValueKind.Number
                    && canonical.TryGetDecimal(out decimal a) && value.TryGetDecimal(out decimal b) ? a == b : canonical.GetRawText() == value.GetRawText();
                if (!equal) throw new InvalidDataException("Continuous default differs from the adopted contract.");
            }
        RlSourceSnapshot source = RlSourceSnapshot.Load(directory, limits);
        using var declaredCatalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Catalog)));
        var catalog = declaredCatalog.RootElement.Deserialize<RlEpisodeCatalog>(RlFiles.Json)
            ?? throw new InvalidDataException("Fixed episode catalog is missing.");
        if (!catalog.Train.SequenceEqual(source.Catalog.Train) || !catalog.Validation.SequenceEqual(source.Catalog.Validation)
            || !catalog.Oos.SequenceEqual(source.Catalog.Oos) || !catalog.Excluded.SequenceEqual(source.Catalog.Excluded)
            || catalog.TrainCut != source.Catalog.TrainCut || catalog.ValidationCut != source.Catalog.ValidationCut
            || catalog.EvaluationSpecificationId != source.Catalog.EvaluationSpecificationId || catalog.SchemaVersion != source.Catalog.SchemaVersion)
            throw new InvalidDataException("Declared episode catalog disagrees with independent source support.");
        using var model = new RlQModel(Path.Combine(directory, RlFiles.Model), continuous);
        using var metrics = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Model + RlFiles.EvaluationSuffix)));
        JsonElement report = metrics.RootElement;
        // Version/resource rejection precedes independent financial qualification work.
        RlAllBlockEvaluation.Validate(directory, job, report, model.Metadata, source, costs);
        if (temporal != model.Metadata.ContainsKey("temporal_specification_id")
            || (temporal && (model.Metadata["temporal_specification_id"] != RlTemporalContract.SpecificationId
                || model.Metadata["temporal_snapshot_sha256"] != RlTemporalValidator.JobIdentity(job))))
            throw new RlTemporalException(RlTemporalRejection.IdentityMismatch, "ONNX temporal version/snapshot differs from the captured source, costs and job.");
        RlDirectionDependency? direction = null;
        RlFinancialParityWork? financialProof = null;
        RlTemporalEvidence? temporalEvidence = null;
        if (continuous)
        {
            direction = JsonSerializer.Deserialize<RlDirectionDependency>(File.ReadAllText(Path.Combine(directory, RlContinuousContract.DirectionManifest)), RlFiles.Json)
                ?? throw new InvalidDataException("Frozen direction dependency required.");
            if (!IsHash(direction.ModelId) || direction.ContractHash != RlContract.ContractHash
                || direction.ModelSha256 != ContentHash.OfFile(Path.Combine(directory, RlContinuousContract.DirectionModel))
                || direction.ScalerSha256 != ContentHash.OfFile(Path.Combine(directory, RlContinuousContract.DirectionScaler))
                || direction.ScalerSha256 != ContentHash.OfFile(Path.Combine(directory, RlFiles.Model + RlFiles.ScalerSuffix))
                || model.Metadata["frozen_direction_model_id"] != direction.ModelId || model.Metadata["frozen_direction_sha256"] != direction.ModelSha256)
                throw new InvalidDataException("Frozen direction/scaler identity is incompatible.");
            if (job.GetProperty("contract_hash").GetString() != contractHash
                || job.GetProperty("direction").Deserialize<RlDirectionDependency>(RlFiles.Json) != direction)
                throw new InvalidDataException("Continuous job does not bind its declared direction/contract snapshot.");
            using var frozen = new RlQModel(Path.Combine(directory, RlContinuousContract.DirectionModel));
            if (frozen.Metadata["symbol"] != source.Manifest.Symbol) throw new InvalidDataException("Frozen direction symbol differs.");
            if (temporal)
            {
                temporalEvidence = RlTemporalValidator.ValidateDependency(directory, job, direction, source);
                if (model.Metadata["direction_temporal_sha256"] != direction.TemporalEvidenceSha256)
                    throw new RlTemporalException(RlTemporalRejection.IdentityMismatch, "ONNX dependency proof hash differs.");
            }
            if (model.Metadata.ContainsKey("financial_parity_specification_id"))
                financialProof = RlContinuousParity.ValidateFinancialDirectory(directory, source, costs);
            else RlContinuousParity.ValidateDirectory(directory, source, costs);
        }
        if (model.Metadata["source_revision"] != source.Revision || model.Metadata["symbol"] != source.Manifest.Symbol)
            throw new InvalidDataException("RL ONNX source/symbol identity is mismatched.");
        model.ValidateParity(Path.Combine(directory, RlFiles.Parity));
        if (financialProof is not null)
            RlContinuousParity.RequireExactJson(JsonSerializer.SerializeToElement(financialProof.Evidence, RlFinancialParityContract.TraceJson),
                report.GetProperty("financial_parity"), "Saved financial qualification proof");
        if (temporal && (report.GetProperty("temporal_specification_id").GetString() != RlTemporalContract.SpecificationId
            || report.GetProperty("temporal_snapshot_sha256").GetString() != RlTemporalValidator.JobIdentity(job)))
            throw new RlTemporalException(RlTemporalRejection.IdentityMismatch, "Evaluation temporal qualification snapshot differs.");
        string revision = RequiredString(report, "evaluation_revision");
        string hash = ContentHash.OfFile(Path.Combine(directory, RlFiles.Model));
        if (report.GetProperty("schema_version").GetInt32() != (source.ExpandedEvaluation ? RlAllBlockEvaluationContract.SchemaVersion : RlContract.SchemaVersion) || report.GetProperty("smoke_only").GetBoolean()
            || !report.GetProperty("parity_verified").GetBoolean() || RequiredString(report, "contract_hash") != contractHash
            || RequiredString(report, "source_revision") != source.Revision || RequiredString(report, "symbol") != source.Manifest.Symbol
            || RequiredString(report, "model_sha256") != hash || !IsHash(revision) || model.Metadata["evaluation_revision"] != revision
            || !RlContract.CandidateSeeds.Contains(report.GetProperty("candidate_seed").GetInt32())
            || !ValidRlCheckpoint(report.GetProperty("checkpoint_step").GetInt32(), continuous))
            throw new InvalidDataException("RL independent evaluation identity/qualification evidence is invalid.");
        decimal validation = ValidateRlEpisodes(report.GetProperty("validation_episodes"), source.Catalog.Validation, "validation", continuous);
        decimal oos = ValidateRlEpisodes(report.GetProperty("oos_episodes"), source.Catalog.Oos, "oos", continuous);
        if (RequiredDecimal(report, "validation_mean_return") != validation || RequiredDecimal(report, "oos_mean_return") != oos)
            throw new InvalidDataException("RL evaluation means disagree with all declared independent episode returns.");
        var reportedCosts = report.GetProperty("configuration").Deserialize<BacktestConfiguration>(RlFiles.Json)
            ?? throw new InvalidDataException("RL evaluation configuration is missing.");
        reportedCosts.Validate();
        if (JsonSerializer.Serialize(costs, RlFiles.Json) != JsonSerializer.Serialize(reportedCosts, RlFiles.Json))
            throw new InvalidDataException("Evaluation and training cost/account snapshots differ.");
        var resources = report.GetProperty("resources");
        if (financialProof is not null)
        {
            var work = resources.GetProperty("work").Deserialize<RlWorkEvidence>(RlFiles.Json)
                ?? throw new InvalidDataException("Missing financial qualification resource ledger.");
            if (work.RegistrationReservedTransitions != financialProof.Transitions || work.RegistrationReservedEpisodeStarts != financialProof.EpisodeStarts
                || work.BudgetChargedTransitions != resources.GetProperty("processed_transitions").GetInt32()
                || work.BudgetChargedEpisodeStarts != resources.GetProperty("episode_starts").GetInt32()
                || work.TransitionAttempts != work.SuccessfulFinancialTransitions || work.EpisodeStartAttempts != work.SuccessfulEpisodeStarts
                || work.BudgetChargedTransitions != work.TransitionAttempts + work.RegistrationReservedTransitions
                || work.BudgetChargedEpisodeStarts != work.EpisodeStartAttempts + work.RegistrationReservedEpisodeStarts
                || work.SuccessfulFinancialTransitions < financialProof.Transitions || work.SuccessfulEpisodeStarts < financialProof.EpisodeStarts)
                throw new InvalidDataException("Attempt/success/reserved qualification resource counts disagree.");
        }
        if (resources.GetProperty("processed_transitions").GetInt64() is < 1 or > RlContract.ProcessedTransitionLimit
            || resources.GetProperty("episode_starts").GetInt64() is < 1 or > RlContract.EpisodeStartLimit)
            throw new InvalidDataException("RL resource evidence exceeds adopted whole-job bounds.");
        ValidateRlCandidates(report, source, continuous);
        ValidateRlBaselines(report.GetProperty("baselines"), source, continuous);
        if (continuous) RlConstraintEvaluation.ValidateReport(directory, job, report, model.Metadata, source, costs);
        if (continuous)
            foreach (int seed in RlContract.CandidateSeeds)
                if (report.GetProperty("training_transitions").GetProperty(seed.ToString(CultureInfo.InvariantCulture)).GetInt32() != RlContinuousContract.TrainingTransitionsPerSeed)
                    throw new InvalidDataException("PPO training transitions differ from 20 full rollouts.");
        using var dependencies = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.Dependencies)));
        if (dependencies.RootElement.ValueKind != JsonValueKind.Object || !dependencies.RootElement.EnumerateObject().Any())
            throw new InvalidDataException("Frozen dependency manifest is missing.");
        var packages = dependencies.RootElement.GetProperty("packages").EnumerateObject()
            .ToDictionary(p => p.Name.Replace('_', '-').ToLowerInvariant(), p => p.Value.GetString(), StringComparer.Ordinal);
        if (packages.GetValueOrDefault("stable-baselines3") != RlContract.StableBaselinesVersion
            || packages.GetValueOrDefault("gymnasium") != RlContract.GymnasiumVersion
            || RequiredString(dependencies.RootElement, "torch_device") != "cpu" || !dependencies.RootElement.GetProperty("deterministic_algorithms").GetBoolean()
            || !IsHash(RequiredString(dependencies.RootElement, "source_script_sha256")) || new FileInfo(Path.Combine(directory, RlFiles.Native)).Length == 0)
            throw new InvalidDataException("RL dependency/device/native-weight evidence is incompatible.");
        if (continuous && (!IsHash(RequiredString(dependencies.RootElement, "ppo_collector_sha256"))
            || !IsHash(RequiredString(dependencies.RootElement, "ppo_policy_sha256"))))
            throw new InvalidDataException("PPO collector/policy source identities are required.");
        if (continuous) RlDiagnosticsValidator.Validate(directory, job, report, model.Metadata);
        using var baselineActions = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, RlFiles.BaselineActions)));
        var expectedSma = RlBaselinePolicies.CreateSmaSignals(source);
        foreach (var block in source.Catalog.Train.Concat(source.Catalog.Validation).Concat(source.Catalog.Oos))
        {
            var actions = baselineActions.RootElement.GetProperty(block.Id);
            if (actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() != RlContract.EpisodeHorizon
                || actions.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.Number || !a.TryGetInt32(out int action) || action is < 0 or >= RlContract.ActionCount))
                throw new InvalidDataException("Saved baseline action support is invalid.");
            int decision = 0;
            foreach (var action in actions.EnumerateArray())
                if (action.GetInt32() != (int)expectedSma[block.StartIndex + RlContract.WindowLength - 1 + decision++])
                    throw new InvalidDataException("Saved baseline actions disagree with the registered causal SMA owner.");
        }
        if (temporal && !continuous) temporalEvidence = RlTemporalValidator.LoadAndValidateProducer(directory, source);
        var files = RequiredRlFiles(continuous, temporal).ToDictionary(name => name, name => ContentHash.OfFile(Path.Combine(directory, name)), StringComparer.Ordinal);
        return new RlGeneration(ComputeRlId(directory, files, continuous), source.Manifest.Symbol, source.Revision,
            contractHash, revision, validation, oos, files, DateTimeOffset.UtcNow)
            { Target = continuous ? RlContinuousContract.Target : RlFiles.Target, FrozenDirectionModelId = direction?.ModelId,
                TemporalSpecificationId = temporal ? RlTemporalContract.SpecificationId : null,
                TemporalStatus = temporalEvidence?.Status ?? RlTemporalBoundStatus.Unverified };
    }

    private static decimal ValidateRlEpisodes(JsonElement rows, IReadOnlyList<RlEpisodeBlock> blocks, string partition, bool continuous = false)
    {
        if (rows.ValueKind != JsonValueKind.Array || blocks.Count == 0 || rows.GetArrayLength() != blocks.Count)
            throw new InvalidDataException("Every declared complete independent RL evaluation episode is required.");
        decimal sum = 0m;
        int index = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (RequiredString(row, "block_id") != blocks[index++].Id || RequiredString(row, "partition") != partition)
                throw new InvalidDataException("RL evaluation block identity/order is invalid.");
            int decisions = row.GetProperty("completed_decisions").GetInt32();
            if (!Enum.TryParse<RlTerminalReason>(RequiredString(row, "terminal_reason"), out var reason) || !Enum.IsDefined(reason) || reason == RlTerminalReason.None
                || decisions < 1 || decisions > RlContract.EpisodeHorizon || (reason == RlTerminalReason.Horizon && decisions != RlContract.EpisodeHorizon))
                throw new InvalidDataException("RL episode did not naturally terminate under the declared contract.");
            if (RequiredDecimal(row, "maximum_drawdown") < 0m || RequiredDecimal(row, "residual_quantity") < 0m
                || RequiredDecimal(row, "residual_quantity") > (continuous ? RlContract.MaxPositionQuantity : RlContract.FixedQuantity)
                || RequiredDecimal(row, "costs") < 0m)
                throw new InvalidDataException("RL episode financial diagnostics are invalid.");
            _ = RequiredDecimal(row, "realized_pnl"); _ = RequiredDecimal(row, "unrealized_pnl");
            sum += RequiredDecimal(row, "terminal_return");
        }
        return sum / blocks.Count;
    }

    private static bool ValidRlCheckpoint(int step, bool continuous) => continuous
        ? step is RlContinuousContract.ValidationCheckpointInterval or RlContinuousContract.TrainingTransitionsPerSeed
        : step is RlContract.ValidationCheckpointInterval or RlContract.TrainingTransitionsPerSeed;

    private static void ValidateRlCandidates(JsonElement report, RlSourceSnapshot source, bool continuous)
    {
        var rows = report.GetProperty("candidate_evaluations");
        int checkpoints = RlContract.TrainingTransitionsPerSeed / RlContract.ValidationCheckpointInterval;
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != RlContract.CandidateSeeds.Length * checkpoints)
            throw new InvalidDataException("All three seeds and both validation checkpoints are required.");
        var candidates = new Dictionary<(int Seed, int Step), (decimal Mean, JsonElement Episodes)>();
        foreach (var row in rows.EnumerateArray())
        {
            int seed = row.GetProperty("seed").GetInt32(); int step = row.GetProperty("checkpoint_step").GetInt32();
            if (!RlContract.CandidateSeeds.Contains(seed) || !ValidRlCheckpoint(step, continuous))
                throw new InvalidDataException("Candidate seed/checkpoint is outside the declared experiment.");
            var summary = row.GetProperty("summary"); var episodes = summary.GetProperty("episodes");
            decimal mean = ValidateRlEpisodes(episodes, source.Catalog.Validation, "validation", continuous);
            if (mean != RequiredDecimal(summary, "mean_return") || !candidates.TryAdd((seed, step), (mean, episodes)))
                throw new InvalidDataException("Candidate validation report is duplicated or inconsistent.");
        }
        var winner = candidates.OrderByDescending(c => c.Value.Mean).ThenBy(c => c.Key.Step).ThenBy(c => c.Key.Seed).First();
        if (winner.Key.Seed != report.GetProperty("candidate_seed").GetInt32() || winner.Key.Step != report.GetProperty("checkpoint_step").GetInt32()
            || winner.Value.Mean != RequiredDecimal(report, "validation_mean_return"))
            throw new InvalidDataException("Selected candidate violates validation mean/earliest checkpoint/lower seed precedence.");
        var selected = report.GetProperty("validation_episodes");
        for (int i = 0; i < selected.GetArrayLength(); i++)
            foreach (string field in new[] { "block_id", "terminal_return", "maximum_drawdown", "completed_decisions", "terminal_reason", "residual_quantity", "realized_pnl", "unrealized_pnl", "costs" })
                if (selected[i].GetProperty(field).GetRawText() != winner.Value.Episodes[i].GetProperty(field).GetRawText())
                    throw new InvalidDataException("Selected validation episode evidence differs from its candidate checkpoint.");
    }

    private static void ValidateRlBaselines(JsonElement baselines, RlSourceSnapshot source, bool continuous)
    {
        foreach (string partition in new[] { "validation", "oos" })
        {
            var blocks = partition == "validation" ? source.Catalog.Validation : source.Catalog.Oos;
            var reports = baselines.GetProperty(partition);
            foreach (string policy in RlBaselinePolicies.EvaluationPolicyIds(continuous).Where(p => p != RlContract.RandomBaselineId))
            {
                var report = reports.GetProperty(policy);
                if (ValidateRlEpisodes(report.GetProperty("episodes"), blocks, partition, continuous) != RequiredDecimal(report, "mean_return"))
                    throw new InvalidDataException("Baseline mean disagrees with independent episode results.");
                _ = RequiredDecimal(report, "mean_maximum_drawdown"); _ = RequiredDecimal(report, "worst_maximum_drawdown");
            }
            var random = reports.GetProperty(RlContract.RandomBaselineId);
            var seeds = random.GetProperty("seeds");
            if (seeds.GetArrayLength() != RlContract.CandidateSeeds.Length) throw new InvalidDataException("Three random baseline seed reports are required.");
            var returns = new List<decimal>();
            int i = 0;
            foreach (var seed in seeds.EnumerateArray())
            {
                if (seed.GetProperty("seed").GetInt32() != RlContract.CandidateSeeds[i++] + RlContract.RandomBaselineSeedOffset)
                    throw new InvalidDataException("Random baseline seed stream is incompatible.");
                decimal mean = ValidateRlEpisodes(seed.GetProperty("episodes"), blocks, partition, continuous);
                if (mean != RequiredDecimal(seed, "mean_return")) throw new InvalidDataException("Random seed mean is inconsistent.");
                returns.Add(mean);
            }
            if (returns.Sum() / returns.Count != RequiredDecimal(random, "mean_return")
                || returns.Min() != RequiredDecimal(random, "min_seed_return") || returns.Max() != RequiredDecimal(random, "max_seed_return"))
                throw new InvalidDataException("Random baseline aggregate is inconsistent.");
        }
    }

    private static string RequiredString(JsonElement value, string name)
        => value.GetProperty(name).GetString() is { Length: > 0 } text ? text : throw new InvalidDataException($"RL evidence {name} is required.");
    private static decimal RequiredDecimal(JsonElement value, string name)
        => decimal.TryParse(RequiredString(value, name), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal number)
            ? number : throw new InvalidDataException($"RL decimal-string evidence {name} is invalid.");

    private void PruneRl(string target)
    {
        if (_artifactSettings is not null) return; // R08 production policy permits reviewed manual deletion only.
        var retained = RlProtectedGenerations(out bool verified);
        if (!verified) return;
        foreach (string id in _rlKnown.Keys.ToArray())
        {
            if (_rlKnown[id].Target != target) continue;
            if (retained.ContainsKey(id)) continue;
            try { DeleteRlDirectory(Path.Combine(RlRoot, id)); _rlKnown.Remove(id); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { RecordStoreFailure("rl-prune-delete", ex); }
        }
    }

    private void DeleteRlDirectory(string directory)
    {
        string root = Path.GetFullPath(RlRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("RL cleanup target is outside its owned store.");
        RequireStoreTree(target); DeleteDirectory(target);
    }
}
