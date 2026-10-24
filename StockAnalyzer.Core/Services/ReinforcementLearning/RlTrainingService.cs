using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

public interface IRlTrainingService
{
    Task<RlTrainingResult> RunAsync(RlTrainingJob job, IProgress<RlTrainingProgress>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Owns the frozen local-source job and isolated ML worker.</summary>
public sealed class RlTrainingService : IRlResumableTrainingService
{
    private readonly IRlModelGenerationRegistry _registry;
    private readonly TrainingJobGate _gate;
    private readonly ITrainingResourceSettings? _resourceSettings;
    private readonly string? _dataRoot;
    internal Action<int>? OwnedWorkerStarted { get; init; }
    /// <summary>The folders the one symbol is copied from; null (every window of the application) means the fixed <c>Data/Daily</c> and <c>Data/Metadata</c>.</summary>
    public RlLocalSourceFolders? LocalSourceFolders { get; init; }
    public RlTrainingService(IRlModelGenerationRegistry registry, TrainingJobGate? gate = null,
        ITrainingResourceSettings? resourceSettings = null, string? dataRoot = null)
    { _registry = registry; _gate = gate ?? new TrainingJobGate(); _resourceSettings = resourceSettings; _dataRoot = dataRoot; }

    public Task<RlTrainingResult> RunAsync(RlTrainingJob job, IProgress<RlTrainingProgress>? progress = null, CancellationToken cancellationToken = default)
        => RunCoreAsync(job, false, null, CancellationToken.None, progress, cancellationToken);
    public Task<RlTrainingResult> RunResumableAsync(RlTrainingJob job, CancellationToken pauseRequest,
        IProgress<RlTrainingProgress>? progress = null, CancellationToken ct = default)
        => RunCoreAsync(job, true, null, pauseRequest, progress, ct);
    public Task<RlTrainingResult> ResumeAsync(RlCheckpointReview reviewed, CancellationToken pauseRequest,
        IProgress<RlTrainingProgress>? progress = null, CancellationToken ct = default)
    {
        var root = (_registry as ModelGenerationRegistry ?? throw new NotSupportedException()).ReadCheckpointJob(reviewed);
        var job = new RlTrainingJob(root.GetProperty("symbol").GetString()!,
            root.GetProperty("start_date").Deserialize<DateOnly>(RlFiles.Json), root.GetProperty("end_date_exclusive").Deserialize<DateOnly>(RlFiles.Json),
            root.GetProperty("costs").Deserialize<StockAnalyzer.Core.Models.Backtest.Engine.BacktestConfiguration>(RlFiles.Json)!,
            SmokeOnly: root.GetProperty("smoke_only").GetBoolean())
        { FrozenDirectionModelId = root.GetProperty("direction").GetProperty("model_id").GetString(),
            EvaluationSpecificationId = root.TryGetProperty("evaluation_specification_id", out var spec) ? spec.GetString() : null };
        return RunCoreAsync(job, true, reviewed, pauseRequest, progress, ct);
    }
    private async Task<RlTrainingResult> RunCoreAsync(RlTrainingJob job, bool resumable, RlCheckpointReview? reviewed,
        CancellationToken pauseRequest, IProgress<RlTrainingProgress>? progress, CancellationToken cancellationToken)
    {
        if (resumable && (job.FrozenDirectionModelId is null || _registry is not ModelGenerationRegistry || _registry.RlArtifactPolicy is null))
            throw new InvalidOperationException("Unavailable: owned T16 storage is required for strict continuation.");
        var activeAttempt = Stopwatch.StartNew();
        job = RlTrainingValidation.Freeze(job);
        var limits = reviewed is null ? RlTrainingValidation.FreezeLimits(_resourceSettings?.Snapshot, job.FrozenDirectionModelId is not null)
            : RlCheckpointStore.Read<JsonElement>(Path.Combine(reviewed.RunDirectory, RlFiles.Job)).GetProperty("resource_limits").Deserialize<TrainingResourceLimits>(RlFiles.Json)!;
        var runId = reviewed?.RunId ?? Guid.NewGuid();
        string identity = ContentHash.OfText(JsonSerializer.Serialize(new { job, contract_hash = RlContract.ContractHash }, RlFiles.Json));
        using var lease = _gate.AcquireIdentity(identity);
        decimal previousSeconds = reviewed is null ? 0 : RlCheckpointStore.ReadBudget(reviewed.RunDirectory).ActiveSeconds;
        double remainingSeconds = Math.Min(limits.MaxRunDurationSeconds, RlContract.JobDeadlineSeconds) - (double)previousSeconds;
        if (remainingSeconds <= 0) throw new InvalidOperationException("Unavailable: cumulative active-time limit reached.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(remainingSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var ct = linked.Token;
        bool continuous = job.FrozenDirectionModelId is not null;
        bool expanded = RlAllBlockEvaluationContract.IsExpanded(job.EvaluationSpecificationId);
        bool shortHistory = RlShortHistoryEvaluationContract.IsShortHistory(job.ShortHistorySpecificationId);
        string root =_dataRoot ?? PathDiscovery.ResolveDataPath(null, "Data");
        using var artifactRun = reviewed is null ? await _registry.BeginRlArtifactRunAsync(runId, root, ct).ConfigureAwait(false)
            : await ((ModelGenerationRegistry)_registry).ResumeArtifactRunAsync(reviewed, ct).ConfigureAwait(false);
        string directory = artifactRun?.Directory ?? Path.Combine(root, RlFiles.TrainingFolder, RlFiles.RlFolder, runId.ToString("N"));
        Directory.CreateDirectory(directory);
        string sourceDirectory = Path.Combine(directory, RlFiles.SourceFolder);
        var files = new Dictionary<string, string>
        {
            ["source_manifest"] = RlFiles.SourceManifest, ["bars"] = RlFiles.Bars, ["actions"] = RlFiles.Actions,
            ["model"] = RlFiles.Model, ["native"] = RlFiles.Native, ["parity"] = RlFiles.Parity, ["dependencies"] = RlFiles.Dependencies,
        };
        var wire = new Dictionary<string, object?>
        {
            ["run_id"] = runId.ToString("N"), ["parent_pid"] = Environment.ProcessId,
            ["contract_hash"] = RlContract.ContractHash, ["defaults"] = RlContract.ToWireDefaults(), ["resource_limits"] = limits,
            ["symbol"] = job.Symbol, ["mapped_symbol"] = job.Symbol, ["start_date"] = job.StartDate,
            ["end_date_exclusive"] = job.EndDateExclusive, ["source_directory"] = sourceDirectory,
            ["artifact_directory"] = directory, ["smoke_only"] = job.SmokeOnly, ["costs"] = job.Costs,
            ["files"] = files,
            ["temporal_specification_id"] = RlTemporalContract.SpecificationId,
            ["temporal_producer_revision"] = RlTemporalContract.ProducerRevision,
        };
        string configPath = Path.Combine(directory, RlFiles.Job);
        RlIpcHost? diagnosticHost = null;
        RlCheckpointStore? checkpointStore = null;
        try
        {
            if (resumable)
                checkpointStore = ((ModelGenerationRegistry)_registry).CreateCheckpointStore(directory, runId, () => pauseRequest.IsCancellationRequested,
                    reviewed?.CheckpointId, (decimal)activeAttempt.Elapsed.TotalSeconds);
            ct.ThrowIfCancellationRequested();
            using var directionLease = continuous ? _registry.AcquireRl(job.FrozenDirectionModelId!)
                ?? throw new InvalidOperationException("Unavailable: select a qualified frozen T15 direction.") : null;
            var directionGeneration = continuous ? _registry.GetRlGeneration(job.FrozenDirectionModelId!) : null;
            if (continuous && (directionGeneration?.Target != RlFiles.Target || directionGeneration.Symbol != job.Symbol))
                throw new InvalidOperationException("Unavailable: frozen direction must be T15 for the selected symbol.");
            var directionEvidence = continuous ? _registry.CaptureRlTemporalEvidence(job.FrozenDirectionModelId!)
                ?? throw new RlTemporalException(RlTemporalRejection.MissingEvidence, "Frozen T15 has no validated temporal evidence.") : null;
            RlSourceSnapshot source;
            string evaluationRevision;
            Dictionary<string, string> metadata;
            if (reviewed is not null)
            {
                wire = RlCheckpointStore.Read<Dictionary<string, object?>>(configPath);
                var saved = JsonSerializer.SerializeToElement(wire, RlFiles.Json);
                source = RlSourceSnapshot.Load(sourceDirectory, limits, job.SmokeOnly, ct, job.EvaluationSpecificationId);
                if (source.Revision != saved.GetProperty("source_revision").GetString()) throw new InvalidDataException("Frozen source changed.");
                metadata = saved.GetProperty("metadata").Deserialize<Dictionary<string, string>>(RlFiles.Json)!;
                evaluationRevision = metadata["evaluation_revision"];
            }
            else
            {
            if (expanded)
            {
                wire[RlAllBlockEvaluationContract.MetadataKey] = job.EvaluationSpecificationId;
                wire["evaluation_contract"] = new { specification_id = RlAllBlockEvaluationContract.SpecificationId,
                    schema_version = RlAllBlockEvaluationContract.SchemaVersion };
            }
            if (shortHistory)
            {
                wire[RlShortHistoryEvaluationContract.MetadataKey] = job.ShortHistorySpecificationId;
                wire[RlShortHistoryEvaluationContract.ContractKey] = new { specification_id = RlShortHistoryEvaluationContract.SpecificationId,
                    schema_version = RlShortHistoryEvaluationContract.SchemaVersion, contract_hash = RlShortHistoryEvaluationContract.ContractHash };
            }
            if (continuous)
            {
                string directionPath = Path.Combine(directory, RlContinuousContract.DirectionModel);
                Directory.CreateDirectory(Path.GetDirectoryName(directionPath)!);
                File.Copy(directionLease!.ModelPath, directionPath);
                File.Copy(directionLease.ModelPath + RlFiles.ScalerSuffix, Path.Combine(directory, RlContinuousContract.DirectionScaler));
                await WriteAsync(Path.Combine(directory, RlTemporalContract.DirectionEvidenceFile), directionEvidence, ct).ConfigureAwait(false);
                var dependency = new RlDirectionDependency(directionGeneration!.ModelId,
                    ContentHash.OfFile(directionPath), ContentHash.OfFile(Path.Combine(directory, RlContinuousContract.DirectionScaler)), RlContract.ContractHash)
                    { TemporalEvidenceSha256 = ContentHash.OfFile(Path.Combine(directory, RlTemporalContract.DirectionEvidenceFile)) };
                if (dependency.ModelSha256 != directionGeneration.Files[RlFiles.Model]
                    || dependency.ScalerSha256 != directionGeneration.Files[RlFiles.Model + RlFiles.ScalerSuffix])
                    throw new InvalidDataException("Frozen T15 dependency changed while capturing its bytes.");
                await WriteAsync(Path.Combine(directory, RlContinuousContract.DirectionManifest), dependency, ct).ConfigureAwait(false);
                wire["continuous_defaults"] = RlContinuousContract.ToWireDefaults();
                wire["contract_hash"] = RlContinuousContract.ContractHash;
                wire["direction"] = dependency;
                wire["direction_temporal_file"] = RlTemporalContract.DirectionEvidenceFile;
                wire["trainer_script"] = RlContinuousContract.TrainerScript;
                wire["strict_dependencies"] = resumable;
                wire["diagnostics_specification_id"] = RlDiagnosticsContract.SpecificationId;
                wire["financial_parity_specification_id"] = RlFinancialParityContract.SpecificationId;
                wire[RlConstraintContract.MetadataKey] = RlConstraintContract.SpecificationId;
            }
            await WriteAsync(configPath, wire, ct).ConfigureAwait(false);
            progress?.Report(new(runId, 0, RlTrainingPhases.Source, "Capturing existing local Data/Daily bytes; historical evidence unverified", 0));
            source = await RlLocalSnapshotFactory.PrepareAsync(job, sourceDirectory, limits, ct, LocalSourceFolders).ConfigureAwait(false);
            // Reject the unchanged requested interval before baselines or any ML worker can consume it.
            if (continuous) RlTemporalValidator.RequireSource(directionEvidence!, source);
            if (expanded)
            {
                var estimate = RlAllBlockEvaluation.Estimate(source, job.SmokeOnly);
                RlAllBlockEvaluation.RequireCapacity(estimate, limits);
                wire["evaluation_resource_estimate"] = RlConstraintEvaluation.ToWire(estimate);
                await WriteAsync(configPath, wire, ct).ConfigureAwait(false);
            }
            if (shortHistory)
            {
                // Every complete block is evaluated: reject an over-capacity request before any worker starts, never by dropping blocks.
                var shortEstimate = RlShortHistoryEvaluation.Estimate(source, job.SmokeOnly);
                RlAllBlockEvaluation.RequireCapacity(shortEstimate, limits);
                wire["evaluation_resource_estimate"] = RlConstraintEvaluation.ToWire(shortEstimate);
                await WriteAsync(configPath, wire, ct).ConfigureAwait(false);
            }
            wire["mapped_symbol"] = source.Manifest.MappedSymbol;
            wire["source_revision"] = source.Revision;
            // A continuous (strict) run hashes its whole execution environment here: minutes of silence otherwise.
            progress?.Report(new(runId, 0, RlTrainingPhases.Environment, "Recording the execution environment before the run", 0));
            await RunWorkerAsync(RlFiles.DependenciesScript, configPath, directory, limits, null, ct).ConfigureAwait(false);
            wire["dependencies_sha256"] = ContentHash.OfFile(Path.Combine(directory, RlFiles.Dependencies));
            await WriteAsync(configPath, wire, ct).ConfigureAwait(false);
            if (source.Manifest.Symbol != job.Symbol || source.Manifest.PeriodStartInclusive != job.StartDate
                || source.Manifest.PeriodEndExclusive != job.EndDateExclusive)
                throw new InvalidDataException("Prepared source symbol/date identity differs from requested job.");
            foreach (string name in new[] { RlFiles.SourceManifest, RlFiles.Bars, RlFiles.Actions, RlFiles.RawParquet, RlFiles.MetadataParquet })
                File.Copy(Path.Combine(sourceDirectory, name), Path.Combine(directory, name), overwrite: false);
            await WriteAsync(Path.Combine(directory, RlFiles.Catalog), source.Catalog, ct).ConfigureAwait(false);
            evaluationRevision = ContentHash.OfText(JsonSerializer.Serialize(new { revision = expanded ? RlAllBlockEvaluationContract.SpecificationId
                    : shortHistory ? RlShortHistoryEvaluationContract.SpecificationId : RlFiles.EvaluationRevision,
                source_revision = source.Revision, catalog = source.Catalog, costs = job.Costs,
                contract_hash = continuous ? RlContinuousContract.ContractHash : RlContract.ContractHash, direction_id = job.FrozenDirectionModelId }, RlFiles.Json));
            metadata = RlObservationBuilder.CreateMetadata(job.Symbol, source.Revision, evaluationRevision);
            metadata["temporal_specification_id"] = RlTemporalContract.SpecificationId;
            metadata["temporal_producer_revision"] = RlTemporalContract.ProducerRevision;
            if (expanded) metadata[RlAllBlockEvaluationContract.MetadataKey] = job.EvaluationSpecificationId!;
            if (shortHistory) metadata[RlShortHistoryEvaluationContract.MetadataKey] = job.ShortHistorySpecificationId!;
            if (continuous)
            {
                metadata["rl_contract_hash"] = RlContinuousContract.ContractHash;
                metadata["target_type"] = RlContinuousContract.Target;
                metadata["frozen_direction_model_id"] = directionGeneration!.ModelId;
                metadata["frozen_direction_sha256"] = ContentHash.OfFile(Path.Combine(directory, RlContinuousContract.DirectionModel));
                metadata["direction_temporal_sha256"] = ContentHash.OfFile(Path.Combine(directory, RlTemporalContract.DirectionEvidenceFile));
                metadata["diagnostics_specification_id"] = RlDiagnosticsContract.SpecificationId;
                metadata["diagnostics_dependencies_sha256"] = (string)wire["dependencies_sha256"]!;
                metadata["financial_parity_specification_id"] = RlFinancialParityContract.SpecificationId;
                metadata[RlConstraintContract.MetadataKey] = RlConstraintContract.SpecificationId;
                File.Copy(Path.Combine(directory, RlContinuousContract.DirectionScaler), Path.Combine(directory, RlFiles.Model + RlFiles.ScalerSuffix));
            }
            wire["catalog"] = source.Catalog; wire["metadata"] = metadata;
            metadata["temporal_snapshot_sha256"] = RlTemporalValidator.JobIdentity(JsonSerializer.SerializeToElement(wire, RlFiles.Json));
            await WriteAsync(configPath, wire, ct).ConfigureAwait(false);
            }
            using var host = new RlIpcHost(runId, source, job.Costs, directory, metadata, progress, continuous);
            if (resumable)
            {
                host.Checkpoints = checkpointStore;
                checkpointStore!.RestoreExports();
                var runtime = new Dictionary<string, object?>(wire) { ["parent_pid"] = Environment.ProcessId,
                    ["checkpoint_contract"] = new { specification_id = RlCheckpointContract.SpecificationId, schema_version = RlCheckpointContract.SchemaVersion,
                        python_file = RlCheckpointContract.PythonState, phases = RlCheckpointContract.Phases },
                    ["resume_path"] = checkpointStore.ResumePath };
                configPath = Path.Combine(directory, RlCheckpointContract.WorkerState);
                await WriteAsync(configPath, runtime, ct).ConfigureAwait(false);
            }
            diagnosticHost = host;
            progress?.Report(new(runId, 0, RlTrainingPhases.Baselines, "Evaluating identical-account baseline episodes", 0));
            if (reviewed is null) host.EvaluateBaselines(ct);
            // The owned (resumable) start verifies the recorded environment again before the worker may start.
            if (resumable) progress?.Report(new(runId, 0, RlTrainingPhases.Environment, "Verifying the recorded execution environment", 0));
            await RunWorkerAsync(continuous ? RlContinuousContract.TrainerScript : RlFiles.TrainerScript, configPath, directory, limits, host, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            using (var model = new RlQModel(Path.Combine(directory, RlFiles.Model), continuous)) model.ValidateParity(Path.Combine(directory, RlFiles.Parity));
            RlFinancialParityWork? financialWork = null;
            if (continuous)
            {
                int maximumTransitions = checked(source.Catalog.Validation.Length * RlContract.EpisodeHorizon * RlFinancialParityContract.ReplayLayers);
                int maximumStarts = checked(source.Catalog.Validation.Length * RlFinancialParityContract.ReplayLayers);
                int repeats = job.SmokeOnly ? 1 : 2;
                host.RequireQualificationCapacity(checked(maximumTransitions * repeats), checked(maximumStarts * repeats));
                financialWork = RlContinuousParity.ValidateFinancialDirectory(directory, source, job.Costs, ct, host.AccountQualificationWork);
                // This pass is attempted/success-counted; the exact independent registry repetition is separately reserved.
                if (!job.SmokeOnly) host.CountQualificationTransitions(financialWork.Transitions, financialWork.EpisodeStarts);
            }
            var report = host.CreateReport(job.SmokeOnly, evaluationRevision);
            report["parity_verified"] = true;
            report["model_sha256"] = ContentHash.OfFile(Path.Combine(directory, RlFiles.Model));
            report["temporal_specification_id"] = RlTemporalContract.SpecificationId;
            report["temporal_snapshot_sha256"] = metadata["temporal_snapshot_sha256"];
            if (financialWork is not null) report["financial_parity"] = JsonSerializer.SerializeToElement(financialWork.Evidence, RlFinancialParityContract.TraceJson);
            string evaluationPath = Path.Combine(directory, RlFiles.Model + RlFiles.EvaluationSuffix);
            await WriteAsync(evaluationPath, report, ct).ConfigureAwait(false);
            if (continuous)
            {
                RlDiagnosticsValidator.Validate(directory, JsonSerializer.SerializeToElement(wire, RlFiles.Json),
                    JsonSerializer.SerializeToElement(report, RlFiles.Json), metadata);
                RlConstraintEvaluation.ValidateReport(directory, JsonSerializer.SerializeToElement(wire, RlFiles.Json),
                    JsonSerializer.SerializeToElement(report, RlFiles.Json), metadata, source, job.Costs);
            }
            if (!continuous)
                await WriteAsync(Path.Combine(directory, RlTemporalContract.EvidenceFile),
                    RlTemporalValidator.Reconstruct(directory, source), ct).ConfigureAwait(false);
            RlAllBlockEvaluation.Validate(directory, JsonSerializer.SerializeToElement(wire, RlFiles.Json),
                JsonSerializer.SerializeToElement(report, RlFiles.Json), metadata, source, job.Costs);
            RlShortHistoryEvaluation.Validate(JsonSerializer.SerializeToElement(wire, RlFiles.Json),
                JsonSerializer.SerializeToElement(report, RlFiles.Json), metadata, source);
            var generation = job.SmokeOnly ? null : await _registry.RegisterRlAsync(directory, ct).ConfigureAwait(false);
            checkpointStore?.Complete();
            if (artifactRun is not null) await artifactRun.FinishAsync(RlArtifactRunState.Completed, generation?.ModelId, ct).ConfigureAwait(false);
            progress?.Report(new(runId, 0, "Complete", job.SmokeOnly ? "Smoke passed; activation prohibited" : "Qualified generation registered; manual activation available", host.Transitions));
            return new(runId, directory, Path.Combine(directory, RlFiles.Model), evaluationPath, generation?.ModelId, job.SmokeOnly, source.Revision);
        }
        catch (Exception error)
        {
            bool paused = checkpointStore?.PauseCommitted == true && !cancellationToken.IsCancellationRequested && !deadline.IsCancellationRequested;
            if (artifactRun is not null)
            {
                try { await artifactRun.FinishAsync(cancellationToken.IsCancellationRequested ? RlArtifactRunState.Cancelled : RlArtifactRunState.Failed).ConfigureAwait(false); }
                catch (Exception evidenceError) { System.Diagnostics.Trace.TraceWarning("RL artifact failure evidence: " + evidenceError.Message); }
            }
            if (continuous && diagnosticHost is not null)
            {
                await WriteAsync(Path.Combine(directory, RlDiagnosticsContract.FailureEvidenceFile), diagnosticHost.CreateDiagnostics(), CancellationToken.None).ConfigureAwait(false);
                await WriteAsync(Path.Combine(directory, RlFinancialParityContract.FailureFile), diagnosticHost.CreateFailureTrace(), CancellationToken.None).ConfigureAwait(false);
            }
            await WriteAsync(Path.Combine(directory, "failure.json"), new { run_id = runId, cancelled = cancellationToken.IsCancellationRequested,
                deadline_exceeded = deadline.IsCancellationRequested, state = cancellationToken.IsCancellationRequested ? "Cancelled" : "Failed",
                error = error.Message }, CancellationToken.None).ConfigureAwait(false);
            if (paused) throw new RlTrainingPausedException(runId, directory);
            throw;
        }
        finally { checkpointStore?.Dispose(); }
    }

    private Task RunWorkerAsync(string relativeScript, string configPath, string directory,
        TrainingResourceLimits limits, RlIpcHost? host, CancellationToken ct)
    {
        bool owned = host?.Checkpoints is not null;       // T16: strict identity verified before the start, the owned pid reported after it
        return RlWorkerProcess.RunAsync(relativeScript, configPath, directory, limits, host is null ? null : host.Handle, ct,
            owned ? () => RlExecutionIdentity.Verify(Path.Combine(directory, RlFiles.Dependencies), ct) : null,
            owned ? pid => OwnedWorkerStarted?.Invoke(pid) : null);
    }

    private static Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return AtomicJsonFile.SaveAsync(path, value, RlFiles.Json);
    }
}
