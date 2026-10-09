using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// One portfolio training run on an existing panel snapshot: fits and freezes the scaler, captures the strict execution identity, runs the
/// Python PPO worker against the Core host, validates the exported ONNX actor (strict boundary and native parity), evaluates it against the
/// baselines (<see cref="RlPortfolioEvaluator"/>) and publishes the evidence through <see cref="RlPortfolioArtifactStore"/>. Evaluation only: no registry target, no activation, no continuation (D14).
/// </summary>
/// <param name="MaxSymbols">Asset slots of the model (N_max); null uses the panel's symbol count.</param>
/// <param name="Plan">Partition plan of a short-history run; null (or the default plan) is the behaviour of the evaluation mode and changes nothing.</param>
/// <param name="Transfer">Fine-tune from a stored base run (admission, weights and scaler copied from it, lineage published); null trains from scratch exactly as before.</param>
/// <param name="Layout">Observation variant of the model; null is v1 and changes nothing (the job, the model and the report are those of every earlier run).</param>
/// <param name="SeedOffset">Run-level seed offset added to each candidate seed of the numerical contract (an ablation needs independent training runs; a run is otherwise deterministic).
/// Zero is the run it always was: nothing is added to the identity or to the job.</param>
public sealed record RlPortfolioTrainingRequest(string PanelDirectory, BacktestConfiguration Costs, RlPortfolioSettings Settings,
    bool SmokeOnly = false, int? MaxSymbols = null, RlPanelEvaluationMode Mode = RlPanelEvaluationMode.Standard, RlPanelPartitionPlan? Plan = null,
    RlPortfolioTransferInit? Transfer = null, RlPortfolioObservationLayout? Layout = null, int SeedOffset = 0);

public sealed record RlPortfolioTrainingResult(Guid RunId, string Directory, string ModelPath, string ParityPath,
    int SelectedSeed, int SelectedCheckpoint, string UniverseSha256, string CalendarSha256, string ReportPath, RlPortfolioEvaluationReport Evaluation,
    string PanelDirectory);

public sealed class RlPortfolioTrainingRunner
{
    private const string TrainerScript = "training/train_rl_portfolio.py";
    private readonly TrainingJobGate _gate;
    private readonly string? _dataRoot;

    public RlPortfolioTrainingRunner(TrainingJobGate? gate = null, string? dataRoot = null)
    { _gate = gate ?? new TrainingJobGate(); _dataRoot = dataRoot; }

    public async Task<RlPortfolioTrainingResult> RunAsync(RlPortfolioTrainingRequest request, IProgress<RlTrainingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Costs);
        ArgumentNullException.ThrowIfNull(request.Settings);
        var costs = RlContract.CreateConfiguration(request.Costs);
        var limits = RlTrainingValidation.FreezeLimits(continuous: true);
        _ = RlPortfolioSettingsValidator.Validate(request.Settings, limits);
        if (request.SeedOffset < 0 || request.SeedOffset > int.MaxValue - RlContract.CandidateSeeds.Max())
            throw new ArgumentOutOfRangeException(nameof(request), "The seed offset must keep every candidate seed a non-negative 32-bit integer.");
        if (request.Plan is not null && request.Mode != RlPanelEvaluationMode.ShortHistory)
            throw new ArgumentException($"{RlPortfolioContract.UniverseErrorPrefix} partition plans apply to the short-history evaluation only.", nameof(request));
        var plan = request.Plan is { IsDefault: true } ? null : request.Plan;
        var panel = RlPanelSnapshot.Load(request.PanelDirectory, limits, request.Settings, request.Mode, plan, cancellationToken);
        var layout = request.Layout ?? RlPortfolioObservationLayout.V1;
        int slots = request.MaxSymbols ?? panel.Symbols.Length;
        if (slots < panel.Symbols.Length || slots > request.Settings.MaxSymbols || !RlPortfolioResources.Fits(slots, limits, layout))
            throw new ArgumentException($"{RlPortfolioContract.UniverseErrorPrefix} {slots} model slots do not fit {panel.Symbols.Length} symbols and the configured and resource limits.");
        var store = new RlPortfolioArtifactStore(_dataRoot ?? PathDiscovery.ResolveDataPath(null, "Data"));
        // A fine-tune is admitted before anything is created or started: a base that fails a rule leaves no run folder and no worker.
        string? freezeName = request.Transfer is null ? null : RlPortfolioFreezes.Name(request.Transfer.Freeze);
        DateOnly firstValidation = request.Transfer is null ? default : RlPortfolioTransferAdmission.FirstValidationDate(panel);
        var baseRun = request.Transfer is null ? null : RlPortfolioTransferAdmission.Admit(store, request.Transfer.BaseRunId, firstValidation, layout);
        var identityFields = new { panel.Manifest.UniverseSha256, panel.Manifest.CalendarSha256, costs, slots,
            request.SmokeOnly, request.Mode, contract_hash = layout.ContractHash };
        // A run without a plan keeps its identity byte for byte; a plan adds its method and block counts (the bars it came from do not change the run).
        string runIdentity = plan is null ? JsonSerializer.Serialize(identityFields, RlFiles.Json)
            : JsonSerializer.Serialize(new { run = identityFields, partition_mode = RlPortfolioPartitionEvidence.ModeName(plan.Mode),
                train_blocks = plan.Requirement.TrainBlocks, validation_blocks = plan.Requirement.ValidationBlocks, test_blocks = plan.Requirement.TestBlocks }, RlFiles.Json);
        // Another run-level seed is another run; offset zero adds nothing, so the identity of every earlier run stays.
        if (request.SeedOffset != 0)
            runIdentity = JsonSerializer.Serialize(new { run_sha256 = ContentHash.OfText(runIdentity), seed_offset = request.SeedOffset }, RlFiles.Json);
        // A fine-tune is another run than a scratch run on the same panel, and another run for each base model and freeze.
        string identity = ContentHash.OfText(baseRun is null ? runIdentity
            : JsonSerializer.Serialize(new { run_sha256 = ContentHash.OfText(runIdentity), base_model_sha256 = baseRun.ModelSha256, freeze = freezeName,
                transfer_contract_hash = RlPortfolioTransferContract.ContractHash }, RlFiles.Json));
        using var lease = _gate.AcquireIdentity(identity);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(limits.MaxRunDurationSeconds, RlContract.JobDeadlineMinutes * 60)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var ct = linked.Token;

        var runId = Guid.NewGuid();
        string directory = store.Begin(runId);
        try
        {
            string modelPath = Path.Combine(directory, RlFiles.Model);
            FixedScaler scaler;
            if (baseRun is null)
            {
                progress?.Report(new(runId, 0, "Scaler", "Fitting the fixed observation scaler on training windows", 0));
                scaler = RlPortfolioScaler.Fit(panel, panel.Catalog, layout);
                RlPortfolioScaler.Save(modelPath, scaler);
            }
            else
            {
                // The policy keeps the observation scale it was trained with: the base sidecar is copied byte for byte, never refitted on the new panel.
                progress?.Report(new(runId, 0, "Scaler", "Copying the scaler and the weights of the base model", 0));
                CopyVerified(baseRun.ScalerPath, modelPath + RlFiles.ScalerSuffix, baseRun.ScalerSha256);
                CopyVerified(baseRun.NativePath, Path.Combine(directory, RlPortfolioTransferContract.InitWeightsFile), baseRun.NativeSha256);
                scaler = LoadCopiedScaler(modelPath, baseRun.ModelPath, baseRun.ModelSha256);
            }
            var files = new Dictionary<string, string>
            {
                ["model"] = RlFiles.Model, ["native"] = RlFiles.Native, ["parity"] = RlFiles.Parity, ["dependencies"] = RlFiles.Dependencies,
            };
            var metadata = new Dictionary<string, string>
            {
                [RlPortfolioContract.MetadataContractHash] = layout.ContractHash,
                [RlPortfolioContract.MetadataTargetType] = RlPortfolioContract.TargetType,
                [RlPortfolioContract.MetadataMaxSymbols] = slots.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [PredictionModelMetadata.WindowSizeKey] = RlContract.WindowLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [RlPortfolioContract.MetadataChannels] = layout.MarketChannelCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                [RlPortfolioContract.MetadataUniverseSha256] = panel.Manifest.UniverseSha256,
                [RlPortfolioContract.MetadataCalendarSha256] = panel.Manifest.CalendarSha256,
                [RlPortfolioContract.MetadataEvaluationSpecification] = request.Mode == RlPanelEvaluationMode.ShortHistory
                    ? RlShortHistoryEvaluationContract.SpecificationId : RlFiles.EvaluationRevision,
                [PredictionModelMetadata.ScalerReferenceKey] = RlPortfolioScaler.SidecarName(modelPath),
            };
            var wire = new Dictionary<string, object?>
            {
                ["run_id"] = runId.ToString("N"), ["parent_pid"] = Environment.ProcessId,
                ["contract_hash"] = layout.ContractHash, ["defaults"] = RlContract.ToWireDefaults(),
                ["ppo_defaults"] = RlContinuousContract.ToWireDefaults(), ["portfolio_defaults"] = RlPortfolioContract.ToWireDefaults(),
                ["resource_limits"] = limits, ["n_max"] = slots, ["symbol_count"] = panel.Symbols.Length,
                ["observation_length"] = RlPortfolioResources.ObservationLength(slots, layout), ["symbols"] = panel.Symbols,
                ["artifact_directory"] = directory, ["smoke_only"] = request.SmokeOnly, ["files"] = files,
                ["trainer_script"] = TrainerScript, ["strict_dependencies"] = false, // the strict file-level identity guards continuation across restarts (T16 resumable runs only); D14 excludes it here
                ["metadata"] = metadata,
                ["catalog"] = new
                {
                    train = panel.Catalog.CellBlocks(RlPortfolioContract.TrainPartition, RlPanelSymbolGroup.Train).Select(b => new { id = b.Id }).ToArray(),
                    validation = RlPortfolioSelectionCell.Blocks(panel.Catalog).Select(b => new { id = b.Id }).ToArray(),
                },
            };
            // Only a job of another variant than v1 carries its channel count, so the job file of every v1 run is unchanged.
            if (layout != RlPortfolioObservationLayout.V1) wire["market_channels"] = layout.MarketChannelCount;
            if (request.SeedOffset != 0) wire["seed_offset"] = request.SeedOffset;
            if (baseRun is not null)
            {
                // Only a fine-tune job carries these keys, so the job file of a scratch run is unchanged.
                wire["transfer"] = new Dictionary<string, object?>
                {
                    ["init_weights"] = RlPortfolioTransferContract.InitWeightsFile, ["init_weights_sha256"] = baseRun.NativeSha256, ["freeze"] = freezeName,
                };
                wire["transfer_defaults"] = RlPortfolioTransferContract.ToWireDefaults();
            }
            string configPath = Path.Combine(directory, RlFiles.Job);
            await WriteAsync(configPath, wire, ct).ConfigureAwait(false);
            progress?.Report(new(runId, 0, "Dependencies", "Capturing the strict execution identity", 0));
            await RunWorkerAsync(RlFiles.DependenciesScript, configPath, directory, limits, null, ct).ConfigureAwait(false);
            wire["dependencies_sha256"] = ContentHash.OfFile(Path.Combine(directory, RlFiles.Dependencies));
            await WriteAsync(configPath, wire, ct).ConfigureAwait(false);

            using var host = new RlPortfolioIpcHost(runId, panel, panel.Catalog, scaler, slots, costs, request.Settings.MaxWeightPercent, progress);
            await RunWorkerAsync(TrainerScript, configPath, directory, limits, host, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var selected = host.Selected ?? throw new InvalidDataException("The worker finished without a Core-confirmed candidate selection.");
            // The selection evidence is saved beside the model (hashed in the manifest) so the choice can be audited without the UI.
            await RlPortfolioTrainingSummaries.SaveAsync(Path.Combine(directory, RlPortfolioTrainingSummaries.FileName),
                RlPortfolioTrainingSummaries.Create(runId, request.SmokeOnly, selected, host.Candidates, host.TrainingTransitionsBySeed)).ConfigureAwait(false);
            using (var model = new RlPortfolioActorModel(modelPath))
            {
                if (model.MaxSymbols != slots) throw new InvalidDataException("The exported model has a different slot count than the job.");
                model.ValidateParity(Path.Combine(directory, RlFiles.Parity));
            }
            progress?.Report(new(runId, 0, "Evaluating", "Evaluating the policy and the baselines on every complete block of each cell", 0));
            var evaluation = await Task.Run(() => RlPortfolioEvaluator.Evaluate(panel, modelPath, costs, request.Settings.MaxWeightPercent,
                request.SmokeOnly, ct), ct).ConfigureAwait(false);
            // A report that does not follow from its own rows, panel and model is never published.
            RlPortfolioEvaluationReports.Validate(evaluation, panel.Manifest, request.Mode, evaluation.ModelSha256, costs, request.Settings.MaxWeightPercent, plan, layout);
            string reportPath = Path.Combine(directory, RlPortfolioEvaluationContract.ReportFile);
            await RlPortfolioEvaluationReports.SaveAsync(reportPath, evaluation, ct).ConfigureAwait(false);
            var published = new List<string>
            {
                RlFiles.Job, RlFiles.Dependencies, RlFiles.Model, RlFiles.Model + RlFiles.ScalerSuffix, RlFiles.Native, RlFiles.Parity,
                RlPortfolioEvaluationContract.ReportFile, RlPortfolioTrainingSummaries.FileName,
            };
            if (baseRun is not null)
            {
                // The lineage is read back and validated against the admitted base run before it is published, like the partition evidence below.
                string lineagePath = Path.Combine(directory, RlPortfolioTransferContract.LineageFile);
                await RlPortfolioTransferEvidence.SaveAsync(lineagePath,
                    RlPortfolioTransferEvidence.Create(RlPortfolioTransferContract.FineTuneKind, baseRun, firstValidation, freezeName), ct).ConfigureAwait(false);
                RlPortfolioTransferEvidence.Validate(RlPortfolioTransferEvidence.Read(lineagePath), baseRun, firstValidation);
                published.Add(RlPortfolioTransferContract.InitWeightsFile);
                published.Add(RlPortfolioTransferContract.LineageFile);
            }
            if (RlPortfolioPartitionEvidence.IsPublished(plan))
            {
                // The partition evidence is read back and validated against the panel before it is published.
                string partitionPath = Path.Combine(directory, RlPortfolioPartitionEvidence.FileName);
                await RlPortfolioPartitionEvidence.SaveAsync(partitionPath, RlPortfolioPartitionEvidence.Create(plan!, panel), ct).ConfigureAwait(false);
                RlPortfolioPartitionEvidence.Validate(RlPortfolioPartitionEvidence.Read(partitionPath), panel);
                published.Add(RlPortfolioPartitionEvidence.FileName);
            }
            await store.PublishAsync(runId, panel.Manifest.UniverseSha256, panel.Manifest.CalendarSha256, published, ct).ConfigureAwait(false);
            return new RlPortfolioTrainingResult(runId, directory, modelPath, Path.Combine(directory, RlFiles.Parity), selected.Seed, selected.Checkpoint,
                panel.Manifest.UniverseSha256, panel.Manifest.CalendarSha256, reportPath, evaluation, panel.Directory);
        }
        catch (Exception error)
        {
            // The run folder stays without a manifest: it is listed as Incomplete, with the failure text beside it.
            // The failure text is evidence only: a folder that cannot be written must not hide the original error that is rethrown below.
            try { await WriteAsync(Path.Combine(directory, "failure.json"), new { error = error.Message }, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception write) when (write is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Copies a base-run file into the new run folder and proves the copy is the very file the admission hashed (the store may change between the two).</summary>
    private static void CopyVerified(string source, string destination, string expectedSha256)
    {
        File.Copy(source, destination, overwrite: false);
        if (ContentHash.OfFile(destination) != expectedSha256) throw ChangedAfterAdmission(source);
    }

    /// <summary>Proves the base file is still the very file the admission hashed (the store may change between the two).</summary>
    private static void VerifyUnchanged(string path, string expectedSha256)
    {
        if (ContentHash.OfFile(path) != expectedSha256) throw ChangedAfterAdmission(path);
    }

    private static InvalidDataException ChangedAfterAdmission(string path)
        => new($"{RlPortfolioTransferContract.ErrorPrefix} {Path.GetFileName(path)} of the base run changed after the admission.");

    /// <summary>
    /// The observation scale of a fine-tune: read from the sidecar COPIED into the new run folder (the file that is published and hashed), never from the base
    /// folder. The base ONNX is hashed again right before it is opened for its metadata, because the store may change after the admission.
    /// </summary>
    internal static FixedScaler LoadCopiedScaler(string runModelPath, string baseModelPath, string baseModelSha256)
    {
        VerifyUnchanged(baseModelPath, baseModelSha256);
        using var baseModel = new RlPortfolioActorModel(baseModelPath);
        return RlPortfolioScaler.Load(runModelPath, baseModel.Metadata);
    }

    private static Task RunWorkerAsync(string relativeScript, string configPath, string directory,
        TrainingResourceLimits limits, RlPortfolioIpcHost? host, CancellationToken ct)
        => RlWorkerProcess.RunAsync(relativeScript, configPath, directory, limits, host is null ? null : host.Handle, ct);

    private static Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return AtomicJsonFile.SaveAsync(path, value, RlFiles.Json);
    }
}
