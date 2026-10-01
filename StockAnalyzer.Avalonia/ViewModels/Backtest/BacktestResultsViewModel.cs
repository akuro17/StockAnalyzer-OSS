using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Evaluation;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

public enum BacktestMetricSemantic { Plus, Minus, Neutral }

public sealed class BacktestTradeRow
{
    public required long TradeId { get; init; }
    public required TradeSide SideKind { get; init; }

    /// <summary>The side as the trade list shows it (the <see cref="TradeSide"/> name); logic uses <see cref="SideKind"/>.</summary>
    public string Side => SideKind.ToString();

    public required DateTime EntryTime { get; init; }
    public required decimal EntryPrice { get; init; }
    public required DateTime ExitTime { get; init; }
    public required decimal ExitPrice { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal ClosedNet { get; init; }
    public required BacktestMetricSemantic PnLSemantic { get; init; }
    public required bool IsForcedLiquidation { get; init; }
}

public sealed record BacktestConditionPresentationRow(int SavedIndex, string Text, string Tooltip);

public sealed record BacktestAuditRow(string LabelKey, string ValueText);

/// <summary>One immutable, internally consistent OHLC run artifact used by every Results-tab consumer.</summary>
public sealed class BacktestResultPresentation
{
    public BacktestEvaluationArtifact? Artifact { get; init; }
    public required BacktestResult Result { get; init; }
    public required BacktestReport Report { get; init; }
    public required ImmutableArray<EquityPoint> EquityPoints { get; init; }

    /// <summary>One flag per <see cref="EquityPoints"/> entry: whether that point is in drawdown (below the running high that starts at the initial capital).
    /// Default (unavailable) when the drawdown computation overflowed; computed once per presentation, never per paint.</summary>
    public ImmutableArray<bool> EquityUnderwaterFlags { get; init; } = ImmutableArray<bool>.Empty;

    /// <summary>The metrics grouped in reading order (empty groups are never present); the table the Results tab shows.</summary>
    public ImmutableArray<BacktestMetricGroupPresentation> MetricGroups { get; init; } = ImmutableArray<BacktestMetricGroupPresentation>.Empty;

    /// <summary>The key metrics shown at the top of the Results tab: the very row objects of <see cref="MetricGroups"/>, never a second formatting.</summary>
    public ImmutableArray<BacktestMetricDisplayRow> Summary { get; init; } = ImmutableArray<BacktestMetricDisplayRow>.Empty;

    public required ImmutableArray<BacktestTradeRow> Trades { get; init; }
    public required ImmutableArray<BacktestConditionPresentationRow> EntryConditions { get; init; }
    public required ImmutableArray<BacktestConditionPresentationRow> ExitConditions { get; init; }
    public required ImmutableArray<BacktestConditionPresentationRow> ReverseConditions { get; init; }
    public required ImmutableArray<BacktestAuditRow> AuditRows { get; init; }
    public required ExecutionModel ExecutionMode { get; init; }
    public required string ExecutionModeDescription { get; init; }
    public required string RiskManagementDescription { get; init; }
    public required bool IsNoOp { get; init; }
    public required string ConditionExpression { get; init; }
    public required string? RunFingerprintText { get; init; }
    public required BacktestMetricSemantic EquityLineSemantic { get; init; }
    public required long Revision { get; init; }
}

/// <summary>Pure numeric formatting for immutable result snapshots.</summary>
public static class BacktestMetricFormatter
{
    public static string FormatPercentage(decimal value, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        int[] bits = decimal.GetBits(value);
        bool negative = (bits[3] & unchecked((int)0x80000000)) != 0;
        int scale = (bits[3] >> 16) & 0x7F;
        BigInteger coefficient = (uint)bits[0]
            | ((BigInteger)(uint)bits[1] << 32)
            | ((BigInteger)(uint)bits[2] << 64);

        BigInteger cents;
        if (scale <= 4)
        {
            cents = coefficient * BigInteger.Pow(10, 4 - scale);
        }
        else
        {
            BigInteger divisor = BigInteger.Pow(10, scale - 4);
            BigInteger quotient = BigInteger.DivRem(coefficient, divisor, out BigInteger remainder);
            int halfComparison = (remainder * 2).CompareTo(divisor);
            if (halfComparison > 0 || (halfComparison == 0 && !quotient.IsEven))
            {
                quotient++;
            }
            cents = quotient;
        }

        bool showNegative = negative && !cents.IsZero;
        BigInteger whole = cents / 100;
        int fraction = (int)(cents % 100);
        string decimalSeparator = culture.NumberFormat.NumberDecimalSeparator;
        return $"{(showNegative ? culture.NumberFormat.NegativeSign : string.Empty)}{whole.ToString(CultureInfo.InvariantCulture)}{decimalSeparator}{fraction:D2}%";
    }
}

public partial class BacktestResultsViewModel : ViewModelBase
{
    private const string ExactRiskPercentFormat = "0.############################";
    private readonly ILocalizationService _localizationService;
    private readonly IBacktestReportExporter _reportExporter;
    private readonly IDialogService _dialogService;
    private BacktestResultPresentation? _presentation;

    /// <summary>Live registered conditions (the Indicator Selection tab's own collections). The Registered Conditions
    /// section lists these - not the frozen per-run <see cref="BacktestResultPresentation"/> rows - so a condition is visible
    /// before the first Run and can be deleted from the same row (explicit user decision overriding the earlier
    /// "frozen evidence" design). Assigned once by <see cref="BacktestWindowViewModel"/>.</summary>
    public BacktestIndicatorSelectionViewModel? ConditionSelection { get; set; }

    public BacktestResultPresentation? Presentation => _presentation;
    public ImmutableArray<EquityPoint> EquityPoints => _presentation?.EquityPoints ?? ImmutableArray<EquityPoint>.Empty;
    public ImmutableArray<bool> EquityUnderwaterFlags => _presentation?.EquityUnderwaterFlags ?? ImmutableArray<bool>.Empty;
    public ImmutableArray<BacktestMetricGroupPresentation> MetricGroups => _presentation?.MetricGroups ?? ImmutableArray<BacktestMetricGroupPresentation>.Empty;
    public bool HasMetricGroups => MetricGroups.Length > 0;
    public ImmutableArray<BacktestMetricDisplayRow> Summary => _presentation?.Summary ?? ImmutableArray<BacktestMetricDisplayRow>.Empty;
    public bool HasSummary => Summary.Length > 0;
    public ImmutableArray<BacktestTradeRow> Trades => _presentation?.Trades ?? ImmutableArray<BacktestTradeRow>.Empty;
    public ImmutableArray<BacktestAuditRow> AuditRows => _presentation?.AuditRows ?? ImmutableArray<BacktestAuditRow>.Empty;
    public ImmutableArray<BacktestConditionPresentationRow> EntryConditionEntries => _presentation?.EntryConditions ?? ImmutableArray<BacktestConditionPresentationRow>.Empty;
    public ImmutableArray<BacktestConditionPresentationRow> ExitConditionEntries => _presentation?.ExitConditions ?? ImmutableArray<BacktestConditionPresentationRow>.Empty;
    public ImmutableArray<BacktestConditionPresentationRow> ReverseConditionEntries => _presentation?.ReverseConditions ?? ImmutableArray<BacktestConditionPresentationRow>.Empty;
    public bool HasResult => _presentation is not null;

    /// <summary>The audit card is shown only when the presentation carries an evaluation artifact (legacy report-only views have none).</summary>
    public bool HasAudit => _presentation?.Artifact is not null;
    public bool IsNoOp => _presentation?.IsNoOp == true;
    public long ResultRevision => _presentation?.Revision ?? 0;
    public BacktestMetricSemantic EquityLineSemantic => _presentation?.EquityLineSemantic ?? BacktestMetricSemantic.Neutral;
    public string? RunFingerprintText => _presentation?.RunFingerprintText;
    public string ConditionExpression => _presentation?.ConditionExpression ?? string.Empty;
    public ExecutionModel ExecutionMode => _presentation?.ExecutionMode ?? ExecutionModel.Legacy;
    public string ExecutionModeDescription => _presentation?.ExecutionModeDescription ?? string.Empty;
    public string RiskManagementDescription => _presentation?.RiskManagementDescription ?? string.Empty;
    public BacktestEvaluationArtifact? EvaluationArtifact => _presentation?.Artifact;

    [ObservableProperty]
    private string? _exportStatusMessage;

    public BacktestResultsViewModel(
        ILocalizationService localizationService,
        IBacktestReportExporter reportExporter,
        IDialogService dialogService)
    {
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _reportExporter = reportExporter ?? throw new ArgumentNullException(nameof(reportExporter));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public void Update(BacktestResult result, BacktestReport report, int evaluationStartIndex) =>
        Update(result, report, evaluationStartIndex, ImmutableArray<BacktestConditionEntry>.Empty, isNoOp: false);

    public void Update(
        BacktestResult result,
        BacktestReport report,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp)
        => Update(result, report, evaluationStartIndex, conditions, isNoOp, null);

    public void Update(
        BacktestResult result,
        BacktestReport report,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement)
        => Update(result, report, evaluationStartIndex, conditions, isNoOp, riskManagement, tree: null);

    /// <param name="tree">The condition tree of a tree-strategy run; null for a list-strategy run (the presentation then shows the flat <paramref name="conditions"/> exactly as before).</param>
    public void Update(
        BacktestResult result,
        BacktestReport report,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement,
        BacktestConditionTree? tree)
    {
        BacktestResultPresentation candidate = BuildPresentation(
            null,
            result,
            report,
            evaluationStartIndex,
            conditions,
            isNoOp,
            riskManagement,
            tree,
            unchecked(ResultRevision + 1));
        Publish(candidate);
    }

    public void Update(
        BacktestEvaluationArtifact artifact,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement)
        => Update(artifact, evaluationStartIndex, conditions, isNoOp, riskManagement, tree: null);

    public void Update(
        BacktestEvaluationArtifact artifact,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement,
        BacktestConditionTree? tree)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Report is null)
        {
            throw new ArgumentException("A displayed completed artifact must contain a report.", nameof(artifact));
        }
        BacktestResultPresentation candidate = BuildPresentation(
            artifact,
            artifact.Result,
            artifact.Report,
            evaluationStartIndex,
            conditions,
            isNoOp,
            riskManagement,
            tree,
            unchecked(ResultRevision + 1));
        Publish(candidate);
    }

    public bool TryUpdate(
        BacktestResult result,
        BacktestReport report,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        out Exception? failure)
        => TryUpdate(result, report, evaluationStartIndex, conditions, isNoOp, null, out failure);

    public bool TryUpdate(
        BacktestResult result,
        BacktestReport report,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement,
        out Exception? failure)
        => TryUpdate(result, report, evaluationStartIndex, conditions, isNoOp, riskManagement, tree: null, out failure);

    public bool TryUpdate(
        BacktestResult result,
        BacktestReport report,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement,
        BacktestConditionTree? tree,
        out Exception? failure)
    {
        try
        {
            Update(result, report, evaluationStartIndex, conditions, isNoOp, riskManagement, tree);
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
            return false;
        }
    }

    public bool TryUpdate(
        BacktestEvaluationArtifact artifact,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement,
        out Exception? failure)
        => TryUpdate(artifact, evaluationStartIndex, conditions, isNoOp, riskManagement, tree: null, out failure);

    public bool TryUpdate(
        BacktestEvaluationArtifact artifact,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement,
        BacktestConditionTree? tree,
        out Exception? failure)
    {
        try
        {
            Update(artifact, evaluationStartIndex, conditions, isNoOp, riskManagement, tree);
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
            return false;
        }
    }

    /// <summary>The comparisons of a group in depth-first pre-order (the order of the persisted tree and of the leaf ordinals).</summary>
    private static IEnumerable<BacktestConditionEntry> EnumerateLeaves(BacktestConditionGroup group)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (IBacktestConditionNode child in group.Children)
        {
            if (child is BacktestConditionLeaf leaf)
            {
                yield return leaf.Comparison;
            }
            else
            {
                foreach (BacktestConditionEntry nested in EnumerateLeaves((BacktestConditionGroup)child)) yield return nested;
            }
        }
    }

    private BacktestResultPresentation BuildPresentation(
        BacktestEvaluationArtifact? artifact,
        BacktestResult result,
        BacktestReport report,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement,
        BacktestConditionTree? tree,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(report);
        BacktestReportValidator.Validate(report);
        ExecutionModel? expectedReportModel = result.Configuration.ExecutionModel == ExecutionModel.YFinanceApproximate
            ? ExecutionModel.YFinanceApproximate
            : null;
        if (report.ExecutionModel != expectedReportModel)
        {
            throw new ArgumentException("The report execution model must match the backtest result.", nameof(report));
        }
        BacktestConditionValidator.ValidateRiskManagement(riskManagement);

        ImmutableArray<EquityPoint> equityPoints = evaluationStartIndex <= 0
            ? result.EquityPoints
            : evaluationStartIndex >= result.EquityPoints.Length
                ? ImmutableArray<EquityPoint>.Empty
                : result.EquityPoints[evaluationStartIndex..];

        var trades = ImmutableArray.CreateBuilder<BacktestTradeRow>(result.Trades.Length);
        foreach (BacktestTrade trade in result.Trades)
        {
            trades.Add(new BacktestTradeRow
            {
                TradeId = trade.TradeId,
                SideKind = trade.Side,
                EntryTime = trade.EntryTime,
                EntryPrice = trade.EntryPrice,
                ExitTime = trade.ExitTime,
                ExitPrice = trade.ExitPrice,
                Quantity = trade.Quantity,
                ClosedNet = trade.ClosedNet,
                PnLSemantic = BacktestMetricGroupBuilder.ResolveBySign(trade.ClosedNet),
                IsForcedLiquidation = trade.IsForcedLiquidation,
            });
        }

        string expression = tree is null
            ? BacktestConditionFormatter.FormatExecutionExpression(conditions)
            : BacktestConditionFormatter.FormatTreeExpression(tree);
        var entryConditions = ImmutableArray.CreateBuilder<BacktestConditionPresentationRow>();
        var exitConditions = ImmutableArray.CreateBuilder<BacktestConditionPresentationRow>();
        var reverseConditions = ImmutableArray.CreateBuilder<BacktestConditionPresentationRow>();
        if (tree is not null)
        {
            // Tree run: one row per leaf in canonical root order and pre-order (SavedIndex = that ordinal), bucketed by section.
            int leafOrdinal = 0;
            foreach ((BacktestConditionSection section, TradeSide side) in BacktestConditionTree.CanonicalRoots)
            {
                foreach (BacktestConditionEntry leaf in EnumerateLeaves(tree.Root(section, side)))
                {
                    var row = new BacktestConditionPresentationRow(
                        leafOrdinal,
                        BacktestConditionFormatter.Format(leaf),
                        BacktestConditionFormatter.FormatAudit(leaf, leafOrdinal, section, side, expression));
                    leafOrdinal++;
                    switch (section)
                    {
                        case BacktestConditionSection.Entry:
                            entryConditions.Add(row);
                            break;
                        case BacktestConditionSection.Exit:
                            exitConditions.Add(row);
                            break;
                        default:
                            reverseConditions.Add(row);
                            break;
                    }
                }
            }
        }
        else
        {
            for (int i = 0; i < conditions.Length; i++)
            {
                BacktestConditionEntry condition = conditions[i];
                var row = new BacktestConditionPresentationRow(
                    i,
                    BacktestConditionFormatter.Format(condition),
                    BacktestConditionFormatter.FormatAudit(condition, i, expression));
                switch (condition.Role)
                {
                    case BacktestConditionRole.EntryOnly:
                        entryConditions.Add(row);
                        break;
                    case BacktestConditionRole.ExitOnly:
                        exitConditions.Add(row);
                        break;
                    default:
                        reverseConditions.Add(row);
                        break;
                }
            }
        }

        ImmutableArray<BacktestMetricGroupPresentation> metricGroups = BacktestMetricGroupBuilder.Build(report, _localizationService);

        BacktestMetricSemantic lineSemantic = report.TotalPnL.Status == MetricStatus.Valid && report.TotalPnL.Value is { } totalPnL
            ? BacktestMetricGroupBuilder.ResolveBySign(totalPnL)
            : BacktestMetricSemantic.Neutral;

        return new BacktestResultPresentation
        {
            Artifact = artifact,
            Result = result,
            Report = report,
            EquityPoints = equityPoints,
            EquityUnderwaterFlags = EquityDrawdownClassifier.ComputeUnderwaterFlags(result.Configuration.InitialCapital, equityPoints),
            MetricGroups = metricGroups,
            Summary = BacktestMetricGroupBuilder.BuildSummary(metricGroups),
            Trades = trades.MoveToImmutable(),
            EntryConditions = entryConditions.ToImmutable(),
            ExitConditions = exitConditions.ToImmutable(),
            ReverseConditions = reverseConditions.ToImmutable(),
            AuditRows = BuildAuditRows(artifact),
            ExecutionMode = result.Configuration.ExecutionModel,
            ExecutionModeDescription = result.Configuration.ExecutionModel switch
            {
                ExecutionModel.Legacy => _localizationService.GetString("Backtest_Results_ExecutionMode_Legacy"),
                ExecutionModel.StrictEvidence => _localizationService.GetString("Backtest_Results_ExecutionMode_Strict"),
                ExecutionModel.YFinanceApproximate => _localizationService.GetString("Backtest_Results_ExecutionMode_YFinanceApproximate"),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(result),
                    result.Configuration.ExecutionModel,
                    "Unknown backtest execution model."),
            },
            RiskManagementDescription = BuildRiskManagementDescription(riskManagement),
            IsNoOp = isNoOp,
            ConditionExpression = expression,
            RunFingerprintText = BuildRunFingerprintText(report.RunFingerprint),
            EquityLineSemantic = lineSemantic,
            Revision = revision,
        };
    }

    private string BuildRiskManagementDescription(BacktestRiskManagementSettings? settings)
    {
        string disabled = _localizationService.GetFormattedString(
            "Backtest_Results_RiskDisabled", "Disabled");
        string stop = _localizationService.GetFormattedString(
            "Backtest_Results_RiskStopLoss", "Stop-Loss: {0}", FormatRisk(settings?.StopLossPercent));
        string take = _localizationService.GetFormattedString(
            "Backtest_Results_RiskTakeProfit", "Take-Profit: {0}", FormatRisk(settings?.TakeProfitPercent));
        return $"{stop}; {take}";

        string FormatRisk(decimal? ratio)
        {
            if (ratio is null) return disabled;
            string exactPercent = (ratio.Value * 100m).ToString(ExactRiskPercentFormat, CultureInfo.CurrentCulture) + "%";
            return ratio.Value == 0m
                ? _localizationService.GetFormattedString("Backtest_Results_RiskBreakeven", "{0} (breakeven)", exactPercent)
                : exactPercent;
        }
    }

    private void Publish(BacktestResultPresentation presentation)
    {
        _presentation = presentation;
        ExportStatusMessage = null;
        OnPropertyChanged(nameof(Presentation));
        OnPropertyChanged(nameof(EquityPoints));
        OnPropertyChanged(nameof(EquityUnderwaterFlags));
        OnPropertyChanged(nameof(MetricGroups));
        OnPropertyChanged(nameof(HasMetricGroups));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSummary));
        OnPropertyChanged(nameof(Trades));
        OnPropertyChanged(nameof(AuditRows));
        OnPropertyChanged(nameof(EntryConditionEntries));
        OnPropertyChanged(nameof(ExitConditionEntries));
        OnPropertyChanged(nameof(ReverseConditionEntries));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasAudit));
        OnPropertyChanged(nameof(IsNoOp));
        OnPropertyChanged(nameof(ResultRevision));
        OnPropertyChanged(nameof(EquityLineSemantic));
        OnPropertyChanged(nameof(RunFingerprintText));
        OnPropertyChanged(nameof(ConditionExpression));
        OnPropertyChanged(nameof(ExecutionMode));
        OnPropertyChanged(nameof(ExecutionModeDescription));
        OnPropertyChanged(nameof(RiskManagementDescription));
        OnPropertyChanged(nameof(EvaluationArtifact));
        ExportReportCommand.NotifyCanExecuteChanged();
    }

    private string? BuildRunFingerprintText(BacktestReportRunFingerprint? fingerprint)
    {
        if (fingerprint is null) return null;
        return fingerprint.IsAvailable
            ? _localizationService.GetFormattedString("Backtest_Results_RunFingerprint", "Run fingerprint (SHA-256): {0}", fingerprint.Sha256 ?? string.Empty)
            : _localizationService.GetFormattedString("Backtest_Results_RunFingerprintUnavailable", "Run fingerprint unavailable: {0}", fingerprint.UnavailableReason ?? string.Empty);
    }

    private bool CanExportReport() => _presentation is not null;

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportAsync()
    {
        BacktestResultPresentation? captured = _presentation;
        BacktestReport? capturedReport = captured?.Report;
        if (capturedReport is null) return;

        // Name the versioned evaluation-evidence format before the folder is chosen, so the user knows what will be written.
        string folderTitle = captured?.Artifact is null
            ? _localizationService.GetString("Backtest_Export_SelectFolderTitle")
            : _localizationService.GetFormattedString(
                "Backtest_Export_SelectFolderTitle_Evaluation",
                "Select folder for evaluation evidence JSON (schema v{0})",
                BacktestEvaluationExportEnvelope.CurrentSchemaVersion);
        string? directoryPath = await _dialogService.ShowOpenFolderDialogAsync(folderTitle).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(directoryPath)) return;

        string fileName = captured?.Artifact is null
            ? $"backtest_report_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json"
            : $"backtest_evaluation_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json";
        try
        {
            if (captured?.Artifact is { } artifact)
            {
                await _reportExporter.ExportEvaluationAsync(artifact, directoryPath, fileName).ConfigureAwait(true);
            }
            else
            {
                await _reportExporter.ExportAsync(capturedReport, directoryPath, fileName).ConfigureAwait(true);
            }
            ExportStatusMessage = _localizationService.GetFormattedString("Backtest_Export_Success", "Exported: {0}", fileName);
        }
        catch (Exception ex)
        {
            ExportStatusMessage = _localizationService.GetFormattedString("Backtest_Export_Error", "Failed to export: {0}", ex.Message);
        }
    }

    private ImmutableArray<BacktestAuditRow> BuildAuditRows(BacktestEvaluationArtifact? artifact)
    {
        if (artifact is null) return ImmutableArray<BacktestAuditRow>.Empty;
        BacktestEvaluationMetadata metadata = artifact.Metadata;
        SamplingQualification sampling = artifact.Sampling;
        string notAvailable = _localizationService.GetString("Backtest_Metric_NotAvailable");
        var rows = ImmutableArray.CreateBuilder<BacktestAuditRow>();
        rows.Add(new("Backtest_Audit_RunIdentity", artifact.RunFingerprint.IsAvailable
            ? artifact.RunFingerprint.Sha256 ?? string.Empty
            : LocalizeReason(artifact.RunFingerprint.UnavailableReason)));
        rows.Add(new("Backtest_Audit_ReportIdentity", artifact.ReportIdentity.IsAvailable
            ? artifact.ReportIdentity.Sha256 ?? string.Empty
            : LocalizeReason(artifact.ReportIdentity.UnavailableReason)));
        rows.Add(new("Backtest_Audit_RequestedPeriod", $"{FormatUtc(metadata.RequestedStartUtc)} — {FormatUtc(metadata.RequestedEndUtc)}"));
        rows.Add(new("Backtest_Audit_LastProcessedLabel", metadata.LastProcessedTimestamp is { } last ? FormatUtc(last) : notAvailable));
        rows.Add(new("Backtest_Audit_Sampling", _localizationService.GetFormattedString(
            "Backtest_Audit_SamplingTemplate",
            "{0} ({1})",
            _localizationService.GetString($"Backtest_Audit_SamplingStatus_{sampling.Status}"),
            _localizationService.GetString($"Backtest_Audit_SamplingReason_{sampling.Reason}"))));
        rows.Add(new("Backtest_Audit_Provider", string.IsNullOrWhiteSpace(sampling.ProviderId)
            ? notAvailable
            : $"{sampling.ProviderId} | {sampling.CalendarId} {sampling.CalendarVersion}"));
        rows.Add(new("Backtest_Audit_RiskMode", _localizationService.GetString($"Backtest_Audit_RiskMode_{artifact.RiskExecutionMode}")));
        rows.Add(new("Backtest_Audit_Bootstrap", _localizationService.GetFormattedString(
            "Backtest_Audit_BootstrapTemplate",
            "Seed {0}; B = {1}; executed replicates {2}",
            metadata.BootstrapSeed.ToString(CultureInfo.CurrentCulture),
            metadata.BootstrapIterations.ToString(CultureInfo.CurrentCulture),
            artifact.BootstrapDiagnostics.ExecutedReplicates?.ToString(CultureInfo.CurrentCulture) ?? notAvailable)));
        rows.Add(new("Backtest_Audit_Annualization", _localizationService.GetFormattedString(
            "Backtest_Audit_AnnualizationTemplate",
            "A = {0} bars per year. Bar Sharpe/Sortino subtract the risk-free rate / MAR divided by A. Annualized values and CAGR require verified sampling (current: {1}).",
            metadata.AnnualPeriods.ToString(CultureInfo.CurrentCulture),
            _localizationService.GetString($"Backtest_Audit_SamplingStatus_{sampling.Status}"))));
        rows.Add(new("Backtest_Audit_RatioDrawdown", FormatDrawdown(artifact.RatioDrawdown)));
        rows.Add(new("Backtest_Audit_AmountDrawdown", FormatDrawdown(artifact.AmountDrawdown)));
        return rows.ToImmutable();
    }

    private string FormatDrawdown(DrawdownEpisodeResult result)
    {
        if (result.Episode is not { } episode)
        {
            return _localizationService.GetFormattedString(
                "Backtest_Audit_DrawdownStateTemplate",
                "{0} ({1})",
                _localizationService.GetString($"Backtest_Audit_DrawdownStatus_{result.Status}"),
                LocalizeReason(result.Reason));
        }
        string depth = episode.Unit == MetricUnit.DrawdownRatio
            ? BacktestMetricFormatter.FormatPercentage(episode.Depth)
            : string.Format(CultureInfo.CurrentCulture, "{0:F4}", episode.Depth);
        return _localizationService.GetFormattedString(
            "Backtest_Audit_DrawdownTemplate",
            "Depth {0} ({1}); peak {2}; trough {3}; recovery {4}",
            depth,
            _localizationService.GetString($"Backtest_Audit_Unit_{episode.Unit}"),
            FormatUtc(episode.PeakUtc),
            FormatUtc(episode.TroughUtc),
            episode.RecoveryUtc is { } recovery ? FormatUtc(recovery) : _localizationService.GetString("Backtest_Audit_Unrecovered"));
    }

    /// <summary>Localizes a machine reason code; a code without a resource is shown verbatim rather than hidden.</summary>
    private string LocalizeReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return _localizationService.GetString("Backtest_Metric_NotAvailable");
        string key = $"Backtest_Audit_Reason_{reason}";
        string localized = _localizationService.GetString(key);
        return string.Equals(localized, key, StringComparison.Ordinal) ? reason : localized;
    }

    private static string FormatUtc(DateTime utc) =>
        utc.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
