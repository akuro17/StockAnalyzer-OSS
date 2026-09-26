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

public sealed record BacktestMetricRow
{
    public required string LabelKey { get; init; }
    public required string ValueText { get; init; }
    public required MetricStatus Status { get; init; }
    public required BacktestMetricSemantic Semantic { get; init; }
    public string? ConfidenceIntervalText { get; init; }
}

public enum BacktestMetricSemantic { Plus, Minus, Neutral }

public sealed class BacktestTradeRow
{
    public required long TradeId { get; init; }
    public required string Side { get; init; }
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
    public required ImmutableArray<BacktestMetricRow> Metrics { get; init; }
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
    public ImmutableArray<BacktestMetricRow> Metrics => _presentation?.Metrics ?? ImmutableArray<BacktestMetricRow>.Empty;
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
    {
        BacktestResultPresentation candidate = BuildPresentation(
            null,
            result,
            report,
            evaluationStartIndex,
            conditions,
            isNoOp,
            riskManagement,
            unchecked(ResultRevision + 1));
        Publish(candidate);
    }

    public void Update(
        BacktestEvaluationArtifact artifact,
        int evaluationStartIndex,
        ImmutableArray<BacktestConditionEntry> conditions,
        bool isNoOp,
        BacktestRiskManagementSettings? riskManagement)
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
    {
        try
        {
            Update(result, report, evaluationStartIndex, conditions, isNoOp, riskManagement);
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
    {
        try
        {
            Update(artifact, evaluationStartIndex, conditions, isNoOp, riskManagement);
            failure = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
            return false;
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

        ImmutableArray<BacktestMetricRow> metrics = BuildMetricRows(report).ToImmutableArray();
        var trades = ImmutableArray.CreateBuilder<BacktestTradeRow>(result.Trades.Length);
        foreach (BacktestTrade trade in result.Trades)
        {
            trades.Add(new BacktestTradeRow
            {
                TradeId = trade.TradeId,
                Side = trade.Side.ToString(),
                EntryTime = trade.EntryTime,
                EntryPrice = trade.EntryPrice,
                ExitTime = trade.ExitTime,
                ExitPrice = trade.ExitPrice,
                Quantity = trade.Quantity,
                ClosedNet = trade.ClosedNet,
                PnLSemantic = ResolveSemantic(trade.ClosedNet),
                IsForcedLiquidation = trade.IsForcedLiquidation,
            });
        }

        string expression = BacktestConditionFormatter.FormatExecutionExpression(conditions);
        var entryConditions = ImmutableArray.CreateBuilder<BacktestConditionPresentationRow>();
        var exitConditions = ImmutableArray.CreateBuilder<BacktestConditionPresentationRow>();
        var reverseConditions = ImmutableArray.CreateBuilder<BacktestConditionPresentationRow>();
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

        BacktestMetricSemantic lineSemantic = report.TotalPnL.Status == MetricStatus.Valid && report.TotalPnL.Value is { } totalPnL
            ? ResolveSemantic(totalPnL)
            : BacktestMetricSemantic.Neutral;

        return new BacktestResultPresentation
        {
            Artifact = artifact,
            Result = result,
            Report = report,
            EquityPoints = equityPoints,
            Metrics = metrics,
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
        OnPropertyChanged(nameof(Metrics));
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

    private static BacktestMetricSemantic ResolveSemantic(decimal value) =>
        value > 0m ? BacktestMetricSemantic.Plus : value < 0m ? BacktestMetricSemantic.Minus : BacktestMetricSemantic.Neutral;

    private IEnumerable<BacktestMetricRow> BuildMetricRows(BacktestReport report)
    {
        yield return Format("Backtest_Metric_TotalPnL", report.TotalPnL, false);
        yield return Format("Backtest_Metric_MaxDrawdownAmount", report.MaxDrawdownAmount, false);
        yield return Format("Backtest_Metric_ExpectedPayoff", report.ExpectedPayoff, false);
        yield return Format("Backtest_Metric_TotalReturn", report.TotalReturn, true);
        yield return Format("Backtest_Metric_CAGR", report.CAGR, true);
        yield return Format("Backtest_Metric_WinRate", report.WinRate, true);
        yield return Format("Backtest_Metric_ProfitFactor", report.ProfitFactor, false);
        yield return Format("Backtest_Metric_MaxDrawdown", report.MaxDrawdown, true);
        yield return Format("Backtest_Metric_UlcerIndex", report.UlcerIndex, false);
        yield return Format("Backtest_Metric_BarSharpe", report.BarSharpe, false);
        yield return Format("Backtest_Metric_AnnualizedSharpe", report.AnnualizedSharpe, false);
        yield return Format("Backtest_Metric_AnnualizedSharpeAutocorrelationAdjusted", report.AnnualizedSharpeAutocorrelationAdjusted, false);
        yield return Format("Backtest_Metric_BarSortino", report.BarSortino, false);
        yield return Format("Backtest_Metric_AnnualizedSortino", report.AnnualizedSortino, false);
        BacktestMetricRow adjusted = Format("Backtest_Metric_AnnualizedSortinoAutocorrelationAdjusted", report.AnnualizedSortinoAutocorrelationAdjusted, false);
        string ciText = report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval is { } ci
            ? $"[{ci.Lower:F4}, {ci.Upper:F4}]"
            : _localizationService.GetString("Backtest_Metric_NotAvailable");
        yield return adjusted with { ConfidenceIntervalText = ciText };
        yield return Format("Backtest_Metric_CalmarFullPeriod", report.CalmarFullPeriod, false);
        yield return Format("Backtest_Metric_SQN", report.SQN, false);
        yield return Format("Backtest_Metric_RecoveryFactor", report.RecoveryFactor, false);
    }

    private BacktestMetricRow Format(string labelKey, MetricValue metric, bool isPercentage)
    {
        if (metric.Status != MetricStatus.Valid || metric.Value is not { } value)
        {
            return new BacktestMetricRow
            {
                LabelKey = labelKey,
                ValueText = _localizationService.GetString("Backtest_Metric_NotAvailable"),
                Status = metric.Status,
                Semantic = BacktestMetricSemantic.Neutral,
            };
        }

        return new BacktestMetricRow
        {
            LabelKey = labelKey,
            ValueText = isPercentage ? BacktestMetricFormatter.FormatPercentage(value) : $"{value:F4}",
            Status = metric.Status,
            Semantic = ResolveSemantic(value),
        };
    }
}
