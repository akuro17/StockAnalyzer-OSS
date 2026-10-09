using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>Outcome of a zero-shot evaluation: the run folder with its evidence and the validated report.</summary>
public sealed record RlPortfolioZeroShotResult(Guid RunId, string Directory, string ReportPath, RlPortfolioEvaluationReport Evaluation,
    string PanelDirectory, RlPortfolioBaseRun Base);

/// <summary>The operations on stored base models behind one seam, so the dialog holds no I/O and tests can script it.</summary>
public interface IRlPortfolioTransferService
{
    /// <summary>Verified runs that hold a complete base model, with the period of their data when it is known.</summary>
    ImmutableArray<RlPortfolioBaseRunInfo> ListBaseRuns();

    /// <summary>Admission of a stored run as a base for a target whose validation starts at <paramref name="targetFirstValidationDate"/>; throws <see cref="InvalidDataException"/> with the reason.</summary>
    RlPortfolioBaseRun Admit(Guid baseRunId, DateOnly targetFirstValidationDate);

    /// <summary>Captures the panel of <paramref name="request"/>, admits the base run, evaluates the stored model on it without training and publishes the evidence.</summary>
    Task<RlPortfolioZeroShotResult> EvaluateStoredModelAsync(Guid baseRunId, RlPortfolioRunRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Which symbols of the panel in <paramref name="panelDirectory"/> the stored base run may already have seen (see <see cref="RlPortfolioSymbolOverlap"/>); null when
    /// the base run or either panel manifest is not available. A report only, never a rejection. A default member, so an implementation that predates it keeps compiling and reports nothing.
    /// </summary>
    RlPortfolioSymbolOverlap? SymbolOverlap(Guid baseRunId, string panelDirectory) => null;

    /// <summary>Panels earlier runs stored, newest capture first; a run may reuse one through <see cref="RlPortfolioRunRequest.PanelId"/>. A default member: none.</summary>
    ImmutableArray<RlPortfolioStoredPanelInfo> ListStoredPanels() => ImmutableArray<RlPortfolioStoredPanelInfo>.Empty;

    /// <summary>
    /// The first validation date a run on the stored panel would have under <paramref name="mode"/> and <paramref name="plan"/> (null = the settings decide): the panel is loaded
    /// and fully re-verified, so a missing, damaged or too short panel throws with the reason. A default member: not supported.
    /// </summary>
    DateOnly StoredPanelFirstValidationDate(Guid panelId, RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan)
        => throw new NotSupportedException("This portfolio service cannot reuse a stored panel.");

    /// <summary>Verified runs with an evaluation report, for the comparison entry. A default member: none.</summary>
    ImmutableArray<RlPortfolioComparableRun> ListComparableRuns() => ImmutableArray<RlPortfolioComparableRun>.Empty;

    /// <summary>The paired comparison of the reports of two stored runs (A minus B); throws <see cref="InvalidDataException"/> naming what differs. A default member: not supported.</summary>
    RlPortfolioTransferComparisonResult CompareRuns(Guid runA, Guid runB) => throw new NotSupportedException("This portfolio service cannot compare stored runs.");
}

/// <inheritdoc />
public sealed class RlPortfolioTransferService : IRlPortfolioTransferService
{
    private readonly TrainingJobGate _gate;
    private readonly string? _dataRoot;
    private readonly TrainingResourceLimits _limits;

    /// <summary>Validates the settings against the frozen resource limits at construction, like <see cref="RlPortfolioService"/>.</summary>
    public RlPortfolioTransferService(RlPortfolioSettings settings, TrainingJobGate? gate = null, string? dataRoot = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _limits = RlTrainingValidation.FreezeLimits(continuous: true);
        _ = RlPortfolioSettingsValidator.Validate(settings, _limits);
        _gate = gate ?? new TrainingJobGate();
        _dataRoot = dataRoot;
    }

    public RlPortfolioSettings Settings { get; }

    private RlPortfolioArtifactStore Store() => new(_dataRoot ?? PathDiscovery.ResolveDataPath(null, "Data"));

    public ImmutableArray<RlPortfolioBaseRunInfo> ListBaseRuns() => RlPortfolioBaseRuns.List(Store());

    public RlPortfolioBaseRun Admit(Guid baseRunId, DateOnly targetFirstValidationDate)
        => RlPortfolioTransferAdmission.Admit(Store(), baseRunId, targetFirstValidationDate);

    public ImmutableArray<RlPortfolioStoredPanelInfo> ListStoredPanels() => RlPortfolioStoredRuns.ListPanels(Store());

    public DateOnly StoredPanelFirstValidationDate(Guid panelId, RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan)
    {
        var resolved = RlPortfolioService.ResolvePlan(Settings, mode, plan);
        var panel = RlPanelSnapshot.Load(Store().ExistingPanelDirectory(panelId), _limits, Settings, mode, resolved);
        return RlPortfolioTransferAdmission.FirstValidationDate(panel);
    }

    public ImmutableArray<RlPortfolioComparableRun> ListComparableRuns() => RlPortfolioStoredRuns.ListComparableRuns(Store());

    public RlPortfolioTransferComparisonResult CompareRuns(Guid runA, Guid runB) => RlPortfolioStoredRuns.Compare(Store(), runA, runB);

    public RlPortfolioSymbolOverlap? SymbolOverlap(Guid baseRunId, string panelDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(panelDirectory);
        var store = Store();
        if (store.Find(baseRunId) is not { Status: RlPortfolioArtifactStatus.Verified, Manifest: { } run }) return null;
        var baseManifest = RlPortfolioBaseRuns.PanelManifest(store, run.UniverseSha256, run.CalendarSha256);
        var target = RlPortfolioBaseRuns.ReadPanelManifest(panelDirectory);
        return baseManifest is null || target is null ? null : RlPortfolioSymbolOverlap.Of(baseManifest, target);
    }

    public async Task<RlPortfolioZeroShotResult> EvaluateStoredModelAsync(Guid baseRunId, RlPortfolioRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Costs);
        var costs = RlContract.CreateConfiguration(request.Costs);
        var plan = RlPortfolioService.ResolvePlan(Settings, request.Mode, request.Plan);
        var settings = Settings.WithRunOverrides(request.HoldoutPercent, request.MaxWeightPercent);
        _ = RlPortfolioSettingsValidator.Validate(settings, _limits);
        var store = Store();
        // A stored panel (request.PanelId) is loaded and fully re-verified; otherwise a new one is captured, and only then is it this call's own to remove on a rejection.
        bool captured = request.PanelId is null;
        var panel = request.PanelId is { } stored
            ? await Task.Run(() => RlPanelSnapshot.Load(store.ExistingPanelDirectory(stored), _limits, settings, request.Mode, plan, cancellationToken), cancellationToken).ConfigureAwait(false)
            : await RlPanelSnapshotFactory.PrepareAsync(request.Symbols, request.Start, request.EndExclusive, store.PanelDirectory(Guid.NewGuid()),
                _limits, settings, request.SymbolHoldout, request.Mode, plan, cancellationToken).ConfigureAwait(false);

        // Admission and the slot check come after the capture (the target's first validation date needs its calendar) and before any evaluation.
        // A rejection here happens before any run folder exists: the panel captured above for this call is then an orphan and is removed (it is this call's own).
        DateOnly firstValidation;
        RlPortfolioBaseRun baseRun;
        try
        {
            firstValidation = RlPortfolioTransferAdmission.FirstValidationDate(panel);
            baseRun = RlPortfolioTransferAdmission.Admit(store, baseRunId, firstValidation);
            if (panel.SymbolCount > baseRun.MaxSymbols)
                throw RlPortfolioTransferRejection.Create($"{RlPortfolioTransferContract.ErrorPrefix} the base model has {baseRun.MaxSymbols} slots for {panel.SymbolCount} symbols.");
        }
        catch (InvalidDataException error) when (captured && RlPortfolioTransferRejection.Is(error))
        {
            _ = store.TryRemoveCapturedPanel(panel.Directory);
            throw;
        }

        string identity = ContentHash.OfText(JsonSerializer.Serialize(new { kind = RlPortfolioTransferContract.ZeroShotKind, base_model_sha256 = baseRun.ModelSha256,
            panel.Manifest.UniverseSha256, panel.Manifest.CalendarSha256, costs, request.SmokeOnly, request.Mode,
            partition_mode = plan is null ? null : RlPortfolioPartitionEvidence.ModeName(plan.Mode),
            train_blocks = plan?.Requirement.TrainBlocks, validation_blocks = plan?.Requirement.ValidationBlocks, test_blocks = plan?.Requirement.TestBlocks,
            transfer_contract_hash = RlPortfolioTransferContract.ContractHash }, RlFiles.Json));
        using var lease = _gate.AcquireIdentity(identity);

        var runId = Guid.NewGuid();
        string directory = store.Begin(runId);
        try
        {
            // A report that does not follow from its own rows, panel and model is never published.
            var report = await Task.Run(() => RlPortfolioEvaluator.EvaluateTransferred(panel, baseRun.ModelPath, costs, settings.MaxWeightPercent,
                request.SmokeOnly, cancellationToken), cancellationToken).ConfigureAwait(false);
            RlPortfolioEvaluationReports.Validate(report, panel.Manifest, request.Mode, baseRun.ModelSha256, costs, settings.MaxWeightPercent, plan);
            string reportPath = Path.Combine(directory, RlPortfolioEvaluationContract.ReportFile);
            await RlPortfolioEvaluationReports.SaveAsync(reportPath, report, cancellationToken).ConfigureAwait(false);
            var published = new List<string> { RlPortfolioEvaluationContract.ReportFile };

            string lineagePath = Path.Combine(directory, RlPortfolioTransferContract.LineageFile);
            await RlPortfolioTransferEvidence.SaveAsync(lineagePath, RlPortfolioTransferEvidence.Create(RlPortfolioTransferContract.ZeroShotKind, baseRun, firstValidation),
                cancellationToken).ConfigureAwait(false);
            RlPortfolioTransferEvidence.Validate(RlPortfolioTransferEvidence.Read(lineagePath), baseRun, firstValidation);
            published.Add(RlPortfolioTransferContract.LineageFile);

            if (RlPortfolioPartitionEvidence.IsPublished(plan))
            {
                string partitionPath = Path.Combine(directory, RlPortfolioPartitionEvidence.FileName);
                await RlPortfolioPartitionEvidence.SaveAsync(partitionPath, RlPortfolioPartitionEvidence.Create(plan!, panel), cancellationToken).ConfigureAwait(false);
                RlPortfolioPartitionEvidence.Validate(RlPortfolioPartitionEvidence.Read(partitionPath), panel);
                published.Add(RlPortfolioPartitionEvidence.FileName);
            }
            await store.PublishAsync(runId, panel.Manifest.UniverseSha256, panel.Manifest.CalendarSha256, published, cancellationToken).ConfigureAwait(false);
            return new RlPortfolioZeroShotResult(runId, directory, reportPath, report, panel.Directory, baseRun);
        }
        catch (Exception error)
        {
            // The run folder stays without a manifest: it is listed as Incomplete, with the failure text beside it (exactly like a training run).
            try { await AtomicJsonFile.SaveAsync(Path.Combine(directory, "failure.json"), new { error = error.Message }, RlFiles.Json).ConfigureAwait(false); }
            catch (Exception write) when (write is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
