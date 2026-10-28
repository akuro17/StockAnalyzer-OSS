using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using StockAnalyzer.Core.Common;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.ReinforcementLearning;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Models.Training;

namespace StockAnalyzer.Core.Services.ReinforcementLearning;

/// <summary>
/// One portfolio run as the dialog starts it: the symbols the Start gate allowed, the period, the evaluation mode and the costs.
/// <paramref name="Plan"/> overrides the partition plan of the settings for this run (short-history only); null uses the settings.
/// <paramref name="Transfer"/> makes <see cref="IRlPortfolioService.RunAsync"/> a fine-tune from a stored base run; null trains from scratch. The zero-shot
/// evaluation of <see cref="IRlPortfolioTransferService"/> names its base run in its own argument and ignores this member.
/// <paramref name="PanelId"/> runs on a panel that an earlier run already stored (the folder name below the store's panel folder) instead of capturing a new one:
/// the stored panel is loaded and fully re-verified, <c>Symbols</c>, <c>Start</c>, <c>EndExclusive</c> and <c>SymbolHoldout</c> are then ignored (the stored panel
/// decides), the plan only recomputes the catalog, and a rejected attempt never removes a panel it did not capture. Two runs on one stored panel share the
/// universe and calendar hashes, which is what <see cref="RlPortfolioTransferComparison"/> needs.
/// </summary>
/// <param name="HoldoutPercent">Holdout share of this run when the dialog changed it; null uses the configured share.</param>
/// <param name="MaxWeightPercent">Weight cap of this run when the dialog changed it; null uses the configured cap.</param>
public sealed record RlPortfolioRunRequest(ImmutableArray<string> Symbols, DateOnly Start, DateOnly EndExclusive, bool SymbolHoldout,
    RlPanelEvaluationMode Mode, BacktestConfiguration Costs, bool SmokeOnly, RlPanelPartitionPlan? Plan = null, RlPortfolioTransferInit? Transfer = null,
    Guid? PanelId = null, int? HoldoutPercent = null, int? MaxWeightPercent = null);

/// <summary>The two operations of the portfolio dialog behind one seam, so the view model holds no I/O and tests can drive it with exact preflights.</summary>
public interface IRlPortfolioService
{
    /// <summary>Settings the dialog gates against (symbol maximum, holdout percent).</summary>
    RlPortfolioSettings Settings { get; }

    Task<PanelPreflight> PreflightAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive,
        bool holdoutRequested, RlPanelEvaluationMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// The latest start date whose period meets the catalog rule for the symbols (see <see cref="RlPanelPeriodAdvisor"/>); null when the service offers no suggestion.
    /// A default member, so an implementation that predates it keeps compiling and simply suggests nothing.
    /// </summary>
    Task<RlStartSuggestion?> SuggestStartAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly endExclusive, RlPanelEvaluationMode mode,
        CancellationToken cancellationToken) => Task.FromResult<RlStartSuggestion?>(null);

    /// <summary>
    /// <see cref="SuggestStartAsync(System.Collections.Generic.IReadOnlyList{string}, DateOnly, RlPanelEvaluationMode, CancellationToken)"/> under a partition plan
    /// of this run. A null plan means the same as the overload without one; a service that predates plans suggests nothing for a plan.
    /// </summary>
    Task<RlStartSuggestion?> SuggestStartAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly endExclusive, RlPanelEvaluationMode mode,
        RlPanelPartitionPlan? plan, CancellationToken cancellationToken)
        => plan is null ? SuggestStartAsync(symbols, endExclusive, mode, cancellationToken) : Task.FromResult<RlStartSuggestion?>(null);

    /// <summary>
    /// The preflight under a partition plan of this run (short-history only). A null plan means the same as the overload without one. A default
    /// member, so an implementation that predates it keeps compiling; it supports only the null plan.
    /// </summary>
    Task<PanelPreflight> PreflightAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive,
        bool holdoutRequested, RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, CancellationToken cancellationToken)
        => plan is null ? PreflightAsync(symbols, start, endExclusive, holdoutRequested, mode, cancellationToken)
            : throw new NotSupportedException("This portfolio service does not support partition plans.");

    /// <summary>
    /// The preflight with the holdout share of this run (null = the configured share). A default member, so an implementation that predates it keeps compiling;
    /// it then checks under its own settings.
    /// </summary>
    Task<PanelPreflight> PreflightAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive,
        bool holdoutRequested, RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, int? holdoutPercent, CancellationToken cancellationToken)
        => PreflightAsync(symbols, start, endExclusive, holdoutRequested, mode, plan, cancellationToken);

    /// <summary>Captures the panel snapshot, trains, evaluates and publishes the evidence. Evaluation only: nothing is registered or activated.</summary>
    Task<RlPortfolioTrainingResult> RunAsync(RlPortfolioRunRequest request, IProgress<RlTrainingProgress>? progress, CancellationToken cancellationToken);
}

/// <inheritdoc />
public sealed class RlPortfolioService : IRlPortfolioService
{
    private readonly TrainingJobGate _gate;
    private readonly string? _dataRoot;
    private readonly RlLocalSourceFolders? _sourceFolders;
    private readonly TrainingResourceLimits _limits;

    /// <summary>Validates the settings against the frozen resource limits at construction: a hard-limit violation stops the application start instead of a later run.</summary>
    /// <param name="sourceFolders">The folders the panel is read from (preflight, start suggestion, capture); null keeps the fixed <c>Data/Daily</c> and <c>Data/Metadata</c>.</param>
    public RlPortfolioService(RlPortfolioSettings settings, TrainingJobGate? gate = null, string? dataRoot = null, RlLocalSourceFolders? sourceFolders = null)
    {
        _sourceFolders = sourceFolders;
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _limits = RlTrainingValidation.FreezeLimits(continuous: true);
        Warnings = RlPortfolioSettingsValidator.Validate(settings, _limits);
        _gate = gate ?? new TrainingJobGate();
        _dataRoot = dataRoot;
    }

    public RlPortfolioSettings Settings { get; }

    /// <summary>Standard-band warnings of the settings (for the caller that owns a logger).</summary>
    public System.Collections.Generic.IReadOnlyList<string> Warnings { get; }

    public Task<PanelPreflight> PreflightAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive,
        bool holdoutRequested, RlPanelEvaluationMode mode, CancellationToken cancellationToken)
        => PreflightAsync(symbols, start, endExclusive, holdoutRequested, mode, null, cancellationToken);

    public Task<PanelPreflight> PreflightAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive,
        bool holdoutRequested, RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, CancellationToken cancellationToken)
        => PreflightAsync(symbols, start, endExclusive, holdoutRequested, mode, plan, null, cancellationToken);

    public Task<PanelPreflight> PreflightAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly start, DateOnly endExclusive,
        bool holdoutRequested, RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, int? holdoutPercent, CancellationToken cancellationToken)
        => RlPanelPreflightEvaluator.EvaluateAsync(symbols, start, endExclusive, holdoutRequested, mode, Settings.WithRunOverrides(holdoutPercent, null), _limits,
            cancellationToken, ResolvePlan(mode, plan), _sourceFolders);

    /// <summary>
    /// The single owner of the partition plan of a run: the plan of the request, else the plan of the settings, for the short-history evaluation only.
    /// A default plan is the same run as no plan, so it is passed as null and changes nothing downstream.
    /// </summary>
    internal RlPanelPartitionPlan? ResolvePlan(RlPanelEvaluationMode mode, RlPanelPartitionPlan? requested) => ResolvePlan(Settings, mode, requested);

    /// <summary>The rule of <see cref="ResolvePlan(RlPanelEvaluationMode, RlPanelPartitionPlan?)"/> for every service that runs on the same settings.</summary>
    internal static RlPanelPartitionPlan? ResolvePlan(RlPortfolioSettings settings, RlPanelEvaluationMode mode, RlPanelPartitionPlan? requested)
    {
        if (mode != RlPanelEvaluationMode.ShortHistory)
            return requested is null ? null
                : throw new ArgumentException($"{RlPortfolioContract.UniverseErrorPrefix} partition plans apply to the short-history evaluation only.", nameof(requested));
        var plan = requested ?? RlPanelPartitionPlan.FromSettings(settings);
        return plan.IsDefault ? null : plan;
    }

    public Task<RlStartSuggestion?> SuggestStartAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly endExclusive,
        RlPanelEvaluationMode mode, CancellationToken cancellationToken)
        => SuggestStartAsync(symbols, endExclusive, mode, null, cancellationToken);

    public async Task<RlStartSuggestion?> SuggestStartAsync(System.Collections.Generic.IReadOnlyList<string> symbols, DateOnly endExclusive,
        RlPanelEvaluationMode mode, RlPanelPartitionPlan? plan, CancellationToken cancellationToken)
        => await RlPanelPeriodAdvisor.SuggestAsync(symbols, endExclusive, mode, _limits, cancellationToken, ResolvePlan(mode, plan), _sourceFolders).ConfigureAwait(false);

    public async Task<RlPortfolioTrainingResult> RunAsync(RlPortfolioRunRequest request, IProgress<RlTrainingProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = ResolvePlan(request.Mode, request.Plan);
        var settings = Settings.WithRunOverrides(request.HoldoutPercent, request.MaxWeightPercent);
        _ = RlPortfolioSettingsValidator.Validate(settings, _limits);
        var store = new RlPortfolioArtifactStore(_dataRoot ?? PathDiscovery.ResolveDataPath(null, "Data"));
        // A stored panel is only located here; the runner loads and re-verifies it itself (with the plan), like a freshly captured one.
        bool captured = request.PanelId is null;
        string panelDirectory = request.PanelId is { } stored ? store.ExistingPanelDirectory(stored)
            : (await RlPanelSnapshotFactory.PrepareAsync(request.Symbols, request.Start, request.EndExclusive,
                store.PanelDirectory(Guid.NewGuid()), _limits, settings, request.SymbolHoldout, request.Mode, plan, cancellationToken, _sourceFolders).ConfigureAwait(false)).Directory;
        var runner = new RlPortfolioTrainingRunner(_gate, _dataRoot);
        try
        {
            return await runner.RunAsync(new RlPortfolioTrainingRequest(panelDirectory, request.Costs, settings, request.SmokeOnly, null, request.Mode, plan, request.Transfer),
                progress, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException error) when (captured && RlPortfolioTransferRejection.Is(error))
        {
            // The runner rejected the base before any run folder existed, so the panel captured above for this very attempt is an orphan; the runner never deletes a panel it was handed.
            _ = store.TryRemoveCapturedPanel(panelDirectory);
            throw;
        }
    }
}
