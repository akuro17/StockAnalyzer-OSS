using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Converters;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Configuration;
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

    public decimal EntryValue { get; init; }
    public decimal ExitValue { get; init; }
    public decimal Commission { get; init; }
    public decimal Slippage { get; init; }
    public decimal? PnLPercent { get; init; }
    public BacktestMetricSemantic PnLPercentSemantic { get; init; }
    public decimal MfePrice { get; init; }
    public decimal? MfePercent { get; init; }
    public decimal MaePrice { get; init; }
    public decimal? MaePercent { get; init; }
    public int HoldingBars { get; init; }
    public TimeSpan Duration { get; init; }
    public string EntrySignal { get; init; } = string.Empty;
    public string ExitReason { get; init; } = string.Empty;
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
    public ImmutableArray<BacktestWarning> Warnings { get; init; } = ImmutableArray<BacktestWarning>.Empty;
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

public partial class BacktestResultsViewModel : ViewModelBase, IDisposable
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

    private ImmutableArray<BacktestMetricGroupPresentation> _placeholderGroups;
    private readonly ImmutableArray<BacktestMetricDisplayRow> _placeholderSummary;

    private static readonly Geometry RightPanelOpenGeometry = Geometry.Parse(SharedIconGeometries.RightPanelOpen);
    private static readonly Geometry RightPanelCloseGeometry = Geometry.Parse(SharedIconGeometries.RightPanelClose);

    public BacktestResultPresentation? Presentation => _presentation;
    public ImmutableArray<EquityPoint> EquityPoints => _presentation?.EquityPoints ?? ImmutableArray<EquityPoint>.Empty;
    public ImmutableArray<bool> EquityUnderwaterFlags => _presentation?.EquityUnderwaterFlags ?? ImmutableArray<bool>.Empty;
    public ImmutableArray<BacktestMetricGroupPresentation> MetricGroups => _presentation?.MetricGroups ?? _placeholderGroups;
    public bool HasMetricGroups => MetricGroups.Length > 0;
    public ImmutableArray<BacktestMetricDisplayRow> Summary => _presentation?.Summary ?? _placeholderSummary;
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
    public ImmutableArray<BacktestWarning> Warnings => _presentation?.Warnings ?? ImmutableArray<BacktestWarning>.Empty;
    // Order-dependent bars are shown as two counts in the Evaluation Audit rows, so they are not repeated as one sentence per bar here.
    private IEnumerable<BacktestWarning> DisplayedWarnings => Warnings.Where(w => !OrderDependentBarCounts.IsOrderDependent(w.Code));
    public bool HasWarnings => DisplayedWarnings.Any();
    public string? WarningSummaryText => HasWarnings
        ? string.Join("; ", DisplayedWarnings.Select(w => w.Message))
        : null;

    [ObservableProperty]
    private string? _exportStatusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayEquityDate))]
    [NotifyPropertyChangedFor(nameof(DisplayEquityValue))]
    [NotifyPropertyChangedFor(nameof(DisplayEquityCash))]
    [NotifyPropertyChangedFor(nameof(DisplayEquityMarketValue))]
    [NotifyPropertyChangedFor(nameof(DisplayEquityHeldMargin))]
    private EquityPoint? _hoveredEquityPoint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleButtonGlyph))]
    [NotifyPropertyChangedFor(nameof(ToggleButtonIconGeometry))]
    private bool _isEquityDataPanelExpanded = true;

    public string ToggleButtonGlyph => IsEquityDataPanelExpanded ? ">" : "<";

    public Geometry ToggleButtonIconGeometry => IsEquityDataPanelExpanded
        ? RightPanelCloseGeometry
        : RightPanelOpenGeometry;

    [RelayCommand]
    private void ToggleEquityDataPanel() => IsEquityDataPanelExpanded = !IsEquityDataPanelExpanded;

    private EquityPoint? ActiveEquityPoint => HoveredEquityPoint ?? (EquityPoints.Length > 0 ? EquityPoints[^1] : null);

    public string DisplayEquityDate => ActiveEquityPoint is { } pt
        ? pt.Timestamp.ToString(Views.Backtest.Rendering.EquityLabelFormats.Instant, Views.Backtest.Rendering.EquityLabelFormats.Culture)
        : "-";

    public string DisplayEquityValue => ActiveEquityPoint is { } pt
        ? pt.Equity.ToString(Views.Backtest.Rendering.EquityLabelFormats.Decimal, Views.Backtest.Rendering.EquityLabelFormats.Culture)
        : "-";

    public string DisplayEquityCash => ActiveEquityPoint is { } pt
        ? pt.Cash.ToString(Views.Backtest.Rendering.EquityLabelFormats.Decimal, Views.Backtest.Rendering.EquityLabelFormats.Culture)
        : "-";

    public string DisplayEquityMarketValue => ActiveEquityPoint is { } pt
        ? pt.MarketValue.ToString(Views.Backtest.Rendering.EquityLabelFormats.Decimal, Views.Backtest.Rendering.EquityLabelFormats.Culture)
        : "-";

    public string DisplayEquityHeldMargin => ActiveEquityPoint is { } pt
        ? pt.HeldMargin.ToString(Views.Backtest.Rendering.EquityLabelFormats.Decimal, Views.Backtest.Rendering.EquityLabelFormats.Culture)
        : "-";

    [ObservableProperty] private bool _isTradeIdVisible = true;
    [ObservableProperty] private bool _isSideVisible = true;
    [ObservableProperty] private bool _isEntryTimeVisible = true;
    [ObservableProperty] private bool _isEntryPriceVisible = true;
    [ObservableProperty] private bool _isExitTimeVisible = true;
    [ObservableProperty] private bool _isExitPriceVisible = true;
    [ObservableProperty] private bool _isQuantityVisible = true;
    [ObservableProperty] private bool _isClosedNetVisible = true;
    [ObservableProperty] private bool _isForcedLiquidationVisible = true;

    private List<string> _activeTradeColumns = new(Dialogs.BacktestTradeHistoryColumnChooserViewModel.KnownColumns.Select(c => c.Id));
    public IReadOnlyList<string> ActiveTradeColumns => _activeTradeColumns;

    public void SetActiveTradeColumns(IEnumerable<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var knownSet = new HashSet<string>(Dialogs.BacktestTradeHistoryColumnChooserViewModel.KnownColumns.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        var list = columns.Where(knownSet.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;

        _activeTradeColumns = list;

        var set = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        IsTradeIdVisible = set.Contains("TradeId");
        IsSideVisible = set.Contains("Side");
        IsEntryTimeVisible = set.Contains("EntryTime");
        IsEntryPriceVisible = set.Contains("EntryPrice");
        IsExitTimeVisible = set.Contains("ExitTime");
        IsExitPriceVisible = set.Contains("ExitPrice");
        IsQuantityVisible = set.Contains("Quantity");
        IsClosedNetVisible = set.Contains("ClosedNet");
        IsForcedLiquidationVisible = set.Contains("ForcedLiquidation");

        OnPropertyChanged(nameof(ActiveTradeColumns));

        if (_columnSettingsManager != null)
        {
            _ = _columnSettingsManager.SaveColumnsAsync(_activeTradeColumns, _activeMetricNames);
        }
    }

    [RelayCommand]
    private async Task ShowTradeColumnsChooser()
    {
        var result = await _dialogService.ShowBacktestTradeHistoryColumnChooserDialogAsync(
            ActiveTradeColumns,
            cols => SetActiveTradeColumns(cols));

        if (result != null)
        {
            SetActiveTradeColumns(result);
        }
    }

    private List<string> _activeMetricNames = new(BacktestMetricCatalog.Definitions.Select(d => d.Name));
    public IReadOnlyList<string> ActiveMetricNames => _activeMetricNames;

    public void SetActiveMetricNames(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var catalogNames = new HashSet<string>(BacktestMetricCatalog.Definitions.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = names
            .Where(n => catalogNames.Contains(n) && set.Add(n))
            .ToList();
        if (resolved.Count == 0) return;

        _activeMetricNames = resolved;

        if (_presentation != null)
        {
            var fullGroups = BacktestMetricGroupBuilder.Build(_presentation.Report, _localizationService, null);
            var updatedGroups = BacktestMetricGroupBuilder.FilterGroups(fullGroups, _activeMetricNames);
            _presentation = new BacktestResultPresentation
            {
                Artifact = _presentation.Artifact,
                Result = _presentation.Result,
                Report = _presentation.Report,
                EquityPoints = _presentation.EquityPoints,
                EquityUnderwaterFlags = _presentation.EquityUnderwaterFlags,
                MetricGroups = updatedGroups,
                Summary = _presentation.Summary,
                Trades = _presentation.Trades,
                EntryConditions = _presentation.EntryConditions,
                ExitConditions = _presentation.ExitConditions,
                ReverseConditions = _presentation.ReverseConditions,
                AuditRows = _presentation.AuditRows,
                ExecutionMode = _presentation.ExecutionMode,
                ExecutionModeDescription = _presentation.ExecutionModeDescription,
                RiskManagementDescription = _presentation.RiskManagementDescription,
                IsNoOp = _presentation.IsNoOp,
                ConditionExpression = _presentation.ConditionExpression,
                RunFingerprintText = _presentation.RunFingerprintText,
                EquityLineSemantic = _presentation.EquityLineSemantic,
                Revision = unchecked(_presentation.Revision + 1),
            };
        }
        else
        {
            var fullPlaceholders = BacktestMetricGroupBuilder.BuildPlaceholder(_localizationService, null);
            _placeholderGroups = BacktestMetricGroupBuilder.FilterGroups(fullPlaceholders, _activeMetricNames);
        }

        OnPropertyChanged(nameof(ActiveMetricNames));
        OnPropertyChanged(nameof(MetricGroups));
        OnPropertyChanged(nameof(HasMetricGroups));

        if (_columnSettingsManager != null)
        {
            _ = _columnSettingsManager.SaveColumnsAsync(_activeTradeColumns, _activeMetricNames);
        }
    }

    [RelayCommand]
    private async Task ShowMetricsChooser()
    {
        var result = await _dialogService.ShowBacktestMetricsColumnChooserDialogAsync(
            ActiveMetricNames,
            cols => SetActiveMetricNames(cols));

        if (result != null)
        {
            SetActiveMetricNames(result);
        }
    }

    private readonly IBacktestColumnSettingsManager? _columnSettingsManager;

    public BacktestResultsViewModel(
        ILocalizationService localizationService,
        IBacktestReportExporter reportExporter,
        IDialogService dialogService,
        IBacktestColumnSettingsManager? columnSettingsManager = null)
    {
        _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
        _reportExporter = reportExporter ?? throw new ArgumentNullException(nameof(reportExporter));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _columnSettingsManager = columnSettingsManager;

        if (_columnSettingsManager != null)
        {
            var loaded = _columnSettingsManager.Load();
            if (loaded != null)
            {
                if (loaded.TradeVisibleColumns is { Count: > 0 } tradeCols)
            {
                var knownSet = new HashSet<string>(Dialogs.BacktestTradeHistoryColumnChooserViewModel.KnownColumns.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
                var validTradeCols = tradeCols.Where(knownSet.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (validTradeCols.Count > 0)
                {
                    _activeTradeColumns = validTradeCols;
                    var set = new HashSet<string>(_activeTradeColumns, StringComparer.OrdinalIgnoreCase);
                    _isTradeIdVisible = set.Contains("TradeId");
                    _isSideVisible = set.Contains("Side");
                    _isEntryTimeVisible = set.Contains("EntryTime");
                    _isEntryPriceVisible = set.Contains("EntryPrice");
                    _isExitTimeVisible = set.Contains("ExitTime");
                    _isExitPriceVisible = set.Contains("ExitPrice");
                    _isQuantityVisible = set.Contains("Quantity");
                    _isClosedNetVisible = set.Contains("ClosedNet");
                    _isForcedLiquidationVisible = set.Contains("ForcedLiquidation");
                }
            }

            if (loaded.MetricVisibleNames is { Count: > 0 } metricNames)
            {
                var catalogSet = new HashSet<string>(BacktestMetricCatalog.Definitions.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
                var validMetrics = metricNames.Where(catalogSet.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (validMetrics.Count > 0)
                {
                    _activeMetricNames = validMetrics;
                }
            }
        }
    }

        ImmutableArray<BacktestMetricGroupPresentation> fullPlaceholders = BacktestMetricGroupBuilder.BuildPlaceholder(_localizationService);
        _placeholderGroups = BacktestMetricGroupBuilder.FilterGroups(fullPlaceholders, _activeMetricNames);
        _placeholderSummary = BacktestMetricGroupBuilder.BuildSummary(fullPlaceholders);

        RebuildTradePageSizeOptions();
        LocalizationManager.Instance.LocaleChanged += OnLocaleChanged;

        UpdatePagedTrades();
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
                EntryValue = trade.EntryValue,
                ExitValue = trade.ExitValue,
                Commission = trade.TotalCommission,
                Slippage = trade.TotalSlippage,
                PnLPercent = trade.PnLPercent,
                PnLPercentSemantic = trade.PnLPercent.HasValue ? BacktestMetricGroupBuilder.ResolveBySign(trade.PnLPercent.Value) : BacktestMetricSemantic.Neutral,
                MfePrice = trade.MfePrice,
                MfePercent = trade.MfePercent,
                MaePrice = trade.MaePrice,
                MaePercent = trade.MaePercent,
                HoldingBars = trade.HoldingBars,
                Duration = trade.Duration,
                EntrySignal = trade.EntrySignal,
                ExitReason = trade.ExitReason,
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

        ImmutableArray<BacktestMetricGroupPresentation> fullGroups = BacktestMetricGroupBuilder.Build(report, _localizationService, null);
        ImmutableArray<BacktestMetricGroupPresentation> metricGroups = BacktestMetricGroupBuilder.FilterGroups(fullGroups, _activeMetricNames);

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
            Summary = BacktestMetricGroupBuilder.BuildSummary(fullGroups),
            Trades = trades.MoveToImmutable(),
            EntryConditions = entryConditions.ToImmutable(),
            ExitConditions = exitConditions.ToImmutable(),
            ReverseConditions = reverseConditions.ToImmutable(),
            AuditRows = BuildAuditRows(artifact, result, evaluationStartIndex, equityPoints.Length),
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
            Warnings = result.Warnings,
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
        OnPropertyChanged(nameof(DisplayEquityDate));
        OnPropertyChanged(nameof(DisplayEquityValue));
        OnPropertyChanged(nameof(DisplayEquityCash));
        OnPropertyChanged(nameof(DisplayEquityMarketValue));
        OnPropertyChanged(nameof(DisplayEquityHeldMargin));
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(WarningSummaryText));
        _currentTradePage = 1;
        UpdatePagedTrades();
        ExportReportCommand.NotifyCanExecuteChanged();
    }

    private string? BuildRunFingerprintText(BacktestReportRunFingerprint? fingerprint)
    {
        if (fingerprint is null) return null;
        return fingerprint.IsAvailable
            ? _localizationService.GetFormattedString("Backtest_Results_RunFingerprint", "{0}", fingerprint.Sha256 ?? string.Empty)
            : _localizationService.GetFormattedString("Backtest_Results_RunFingerprintUnavailable", "Unavailable ({0})", fingerprint.UnavailableReason ?? string.Empty);
    }

    private bool CanExportReport() => _presentation is not null;

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private async Task ExportReportAsync()
    {
        BacktestResultPresentation? captured = _presentation;
        if (captured is null) return;

        string defaultFileName = $"backtest_trades_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";
        string title = _localizationService.GetString("Backtest_Export_SaveFileTitle");
        if (string.IsNullOrWhiteSpace(title) || title == "Backtest_Export_SaveFileTitle")
        {
            title = _localizationService.GetString("Backtest_Export_SelectFolderTitle");
        }

        string? filePath = await _dialogService.ShowSaveFileDialogAsync(
            title,
            defaultExtension: "csv",
            defaultFilename: defaultFileName,
            filters: ["csv"]).ConfigureAwait(true);

        if (string.IsNullOrWhiteSpace(filePath)) return;

        string fullPath = Path.GetFullPath(filePath);
        if (string.IsNullOrEmpty(Path.GetExtension(fullPath)))
        {
            fullPath += ".csv";
        }

        string directoryPath = Path.GetDirectoryName(fullPath) ?? string.Empty;
        string fileName = Path.GetFileName(fullPath);

        try
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                await _reportExporter.ExportTradesCsvAsync(captured.Result.Trades, fileName).ConfigureAwait(true);
            }
            else
            {
                await _reportExporter.ExportTradesCsvAsync(captured.Result.Trades, directoryPath, fileName).ConfigureAwait(true);
            }
            ExportStatusMessage = _localizationService.GetFormattedString("Backtest_Export_Success", "Exported: {0}", fileName);
        }
        catch (Exception ex)
        {
            ExportStatusMessage = _localizationService.GetFormattedString("Backtest_Export_Error", "Failed to export: {0}", ex.Message);
        }
    }

    private ImmutableArray<BacktestAuditRow> BuildAuditRows(BacktestEvaluationArtifact? artifact, BacktestResult result, int evaluationStartIndex, int evaluatedBarCount)
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
        OrderDependentBarCounts orderDependent = OrderDependentBarCounts.Count(result.Warnings, evaluationStartIndex);
        rows.Add(new("Backtest_Audit_OrderDependentBars", _localizationService.GetFormattedString(
            "Backtest_Audit_OrderDependentBarsTemplate",
            "{0} of {1} bars",
            orderDependent.Detected.ToString(CultureInfo.CurrentCulture),
            evaluatedBarCount.ToString(CultureInfo.CurrentCulture))));
        rows.Add(new("Backtest_Audit_OrderDependentBarsAffecting", _localizationService.GetFormattedString(
            "Backtest_Audit_OrderDependentBarsTemplate",
            "{0} of {1} bars",
            orderDependent.Affecting.ToString(CultureInfo.CurrentCulture),
            evaluatedBarCount.ToString(CultureInfo.CurrentCulture))));
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

    #region Trade History Pagination & Sorting

    private int _currentTradePage = 1;
    public int CurrentTradePage => _currentTradePage;

    private int _totalTradePages = 1;
    public int TotalTradePages => _totalTradePages;

    public int TotalTradeCount => _presentation?.Trades.Length ?? 0;

    private string? _activeSortColumnId;
    public string? ActiveSortColumnId => _activeSortColumnId;

    private TradeSortOrder _activeSortOrder = TradeSortOrder.None;
    public TradeSortOrder ActiveSortOrder => _activeSortOrder;

    public ImmutableArray<TradePageSizeOption> TradePageSizeOptions { get; private set; }

    private TradePageSizeOption _selectedTradePageSize = null!;
    public TradePageSizeOption SelectedTradePageSize
    {
        get => _selectedTradePageSize;
        set
        {
            if (_selectedTradePageSize != value && value != null)
            {
                _selectedTradePageSize = value;
                OnPropertyChanged();
                _currentTradePage = 1;
                UpdatePagedTrades();
            }
        }
    }

    public ImmutableArray<BacktestTradeRow> PagedTrades { get; private set; } = ImmutableArray<BacktestTradeRow>.Empty;
    public ImmutableArray<TradePaginationToken> TradePaginationTokens { get; private set; } = ImmutableArray<TradePaginationToken>.Empty;
    public string TradePaginationInfoText { get; private set; } = string.Empty;

    public bool CanPreviousTradePage => CurrentTradePage > 1;
    public bool CanNextTradePage => CurrentTradePage < TotalTradePages;

    [RelayCommand(CanExecute = nameof(CanGoToTradePage))]
    private void GoToTradePage(int pageNumber)
    {
        if (pageNumber >= 1 && pageNumber <= TotalTradePages && pageNumber != _currentTradePage)
        {
            _currentTradePage = pageNumber;
            UpdatePagedTrades();
        }
    }

    private bool CanGoToTradePage(int pageNumber) => pageNumber >= 1 && pageNumber <= TotalTradePages;

    [RelayCommand(CanExecute = nameof(CanPreviousTradePage))]
    private void PreviousTradePage()
    {
        if (CanPreviousTradePage)
        {
            _currentTradePage--;
            UpdatePagedTrades();
        }
    }

    [RelayCommand(CanExecute = nameof(CanNextTradePage))]
    private void NextTradePage()
    {
        if (CanNextTradePage)
        {
            _currentTradePage++;
            UpdatePagedTrades();
        }
    }

    [RelayCommand]
    private void SortTrades(string? columnId)
    {
        if (string.IsNullOrWhiteSpace(columnId)) return;

        var matched = Dialogs.BacktestTradeHistoryColumnChooserViewModel.KnownColumns
            .FirstOrDefault(c => string.Equals(c.Id, columnId, StringComparison.OrdinalIgnoreCase));
        if (matched.Id == null) return;

        string canonicalColId = matched.Id;
        if (!string.Equals(_activeSortColumnId, canonicalColId, StringComparison.Ordinal))
        {
            _activeSortColumnId = canonicalColId;
            _activeSortOrder = TradeSortOrder.Ascending;
        }
        else if (_activeSortOrder == TradeSortOrder.Ascending)
        {
            _activeSortOrder = TradeSortOrder.Descending;
        }
        else
        {
            _activeSortColumnId = null;
            _activeSortOrder = TradeSortOrder.None;
        }

        _currentTradePage = 1;
        UpdatePagedTrades();
    }

    private void UpdatePagedTrades()
    {
        ImmutableArray<BacktestTradeRow> raw = _presentation?.Trades ?? ImmutableArray<BacktestTradeRow>.Empty;

        IEnumerable<BacktestTradeRow> query = raw;
        if (!string.IsNullOrEmpty(_activeSortColumnId) && _activeSortOrder != TradeSortOrder.None)
        {
            bool asc = _activeSortOrder == TradeSortOrder.Ascending;
            query = _activeSortColumnId switch
            {
                "TradeId" => asc ? query.OrderBy(t => t.TradeId) : query.OrderByDescending(t => t.TradeId),
                "Side" => asc ? query.OrderBy(t => t.SideKind).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.SideKind).ThenBy(t => t.TradeId),
                "EntryTime" => asc ? query.OrderBy(t => t.EntryTime).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.EntryTime).ThenBy(t => t.TradeId),
                "EntryPrice" => asc ? query.OrderBy(t => t.EntryPrice).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.EntryPrice).ThenBy(t => t.TradeId),
                "ExitTime" => asc ? query.OrderBy(t => t.ExitTime).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.ExitTime).ThenBy(t => t.TradeId),
                "ExitPrice" => asc ? query.OrderBy(t => t.ExitPrice).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.ExitPrice).ThenBy(t => t.TradeId),
                "Quantity" => asc ? query.OrderBy(t => t.Quantity).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.Quantity).ThenBy(t => t.TradeId),
                "EntryValue" => asc ? query.OrderBy(t => t.EntryValue).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.EntryValue).ThenBy(t => t.TradeId),
                "ExitValue" => asc ? query.OrderBy(t => t.ExitValue).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.ExitValue).ThenBy(t => t.TradeId),
                "Commission" => asc ? query.OrderBy(t => t.Commission).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.Commission).ThenBy(t => t.TradeId),
                "Slippage" => asc ? query.OrderBy(t => t.Slippage).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.Slippage).ThenBy(t => t.TradeId),
                "ClosedNet" => asc ? query.OrderBy(t => t.ClosedNet).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.ClosedNet).ThenBy(t => t.TradeId),
                "PnLPercent" => asc ? query.OrderBy(t => t.PnLPercent ?? decimal.MinValue).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.PnLPercent ?? decimal.MinValue).ThenBy(t => t.TradeId),
                "MfePrice" => asc ? query.OrderBy(t => t.MfePrice).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.MfePrice).ThenBy(t => t.TradeId),
                "MfePercent" => asc ? query.OrderBy(t => t.MfePercent ?? decimal.MinValue).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.MfePercent ?? decimal.MinValue).ThenBy(t => t.TradeId),
                "MaePrice" => asc ? query.OrderBy(t => t.MaePrice).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.MaePrice).ThenBy(t => t.TradeId),
                "MaePercent" => asc ? query.OrderBy(t => t.MaePercent ?? decimal.MinValue).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.MaePercent ?? decimal.MinValue).ThenBy(t => t.TradeId),
                "HoldingBars" => asc ? query.OrderBy(t => t.HoldingBars).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.HoldingBars).ThenBy(t => t.TradeId),
                "Duration" => asc ? query.OrderBy(t => t.Duration).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.Duration).ThenBy(t => t.TradeId),
                "EntrySignal" => asc ? query.OrderBy(t => t.EntrySignal, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.EntrySignal, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TradeId),
                "ExitReason" => asc ? query.OrderBy(t => t.ExitReason, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.ExitReason, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.TradeId),
                "ForcedLiquidation" => asc ? query.OrderBy(t => t.IsForcedLiquidation).ThenBy(t => t.TradeId) : query.OrderByDescending(t => t.IsForcedLiquidation).ThenBy(t => t.TradeId),
                _ => query
            };
        }

        ImmutableArray<BacktestTradeRow> sorted = query.ToImmutableArray();
        int totalCount = sorted.Length;
        int pageSize = _selectedTradePageSize?.Value ?? 20;
        int totalPages = totalCount == 0 ? 1 : (totalCount + pageSize - 1) / pageSize;
        _totalTradePages = totalPages;

        if (_currentTradePage > totalPages) _currentTradePage = totalPages;
        if (_currentTradePage < 1) _currentTradePage = 1;

        int skip = (_currentTradePage - 1) * pageSize;
        PagedTrades = totalCount == 0
            ? ImmutableArray<BacktestTradeRow>.Empty
            : sorted.Skip(skip).Take(pageSize).ToImmutableArray();

        int startNum = totalCount == 0 ? 0 : skip + 1;
        int endNum = totalCount == 0 ? 0 : Math.Min(skip + pageSize, totalCount);

        string rawTemplate = _localizationService.GetString("Backtest_TradeList_Pagination_Info");
        string template = (string.IsNullOrEmpty(rawTemplate) || rawTemplate == "Backtest_TradeList_Pagination_Info")
            ? "Showing {0} - {1} of {2} items"
            : rawTemplate;

        TradePaginationInfoText = string.Format(CultureInfo.CurrentCulture, template, startNum, endNum, totalCount);

        TradePaginationTokens = ComputePaginationTokens(totalPages, _currentTradePage);

        OnPropertyChanged(nameof(CurrentTradePage));
        OnPropertyChanged(nameof(TotalTradePages));
        OnPropertyChanged(nameof(CanPreviousTradePage));
        OnPropertyChanged(nameof(CanNextTradePage));
        OnPropertyChanged(nameof(ActiveSortColumnId));
        OnPropertyChanged(nameof(ActiveSortOrder));
        OnPropertyChanged(nameof(PagedTrades));
        OnPropertyChanged(nameof(TradePaginationTokens));
        OnPropertyChanged(nameof(TradePaginationInfoText));

        PreviousTradePageCommand.NotifyCanExecuteChanged();
        NextTradePageCommand.NotifyCanExecuteChanged();
    }

    public static ImmutableArray<TradePaginationToken> ComputePaginationTokens(int totalPages, int currentPage)
    {
        var builder = ImmutableArray.CreateBuilder<TradePaginationToken>();
        if (totalPages <= 7)
        {
            for (int p = 1; p <= totalPages; p++)
            {
                builder.Add(new TradePaginationToken(p, IsEllipsis: false, IsActive: p == currentPage));
            }
        }
        else
        {
            if (currentPage <= 4)
            {
                for (int p = 1; p <= 5; p++)
                {
                    builder.Add(new TradePaginationToken(p, IsEllipsis: false, IsActive: p == currentPage));
                }
                builder.Add(new TradePaginationToken(0, IsEllipsis: true, IsActive: false));
                builder.Add(new TradePaginationToken(totalPages, IsEllipsis: false, IsActive: false));
            }
            else if (currentPage >= totalPages - 3)
            {
                builder.Add(new TradePaginationToken(1, IsEllipsis: false, IsActive: false));
                builder.Add(new TradePaginationToken(0, IsEllipsis: true, IsActive: false));
                for (int p = totalPages - 4; p <= totalPages; p++)
                {
                    builder.Add(new TradePaginationToken(p, IsEllipsis: false, IsActive: p == currentPage));
                }
            }
            else
            {
                builder.Add(new TradePaginationToken(1, IsEllipsis: false, IsActive: false));
                builder.Add(new TradePaginationToken(0, IsEllipsis: true, IsActive: false));
                for (int p = currentPage - 1; p <= currentPage + 1; p++)
                {
                    builder.Add(new TradePaginationToken(p, IsEllipsis: false, IsActive: p == currentPage));
                }
                builder.Add(new TradePaginationToken(0, IsEllipsis: true, IsActive: false));
                builder.Add(new TradePaginationToken(totalPages, IsEllipsis: false, IsActive: false));
            }
        }
        return builder.ToImmutable();
    }

    public void RebuildTradePageSizeOptions()
    {
        int selectedValue = _selectedTradePageSize?.Value ?? 20;
        int[] pageSizes = [5, 10, 20, 50, 100];
        var optionsBuilder = ImmutableArray.CreateBuilder<TradePageSizeOption>(pageSizes.Length);
        foreach (int size in pageSizes)
        {
            string rawFmt = _localizationService.GetString("Backtest_TradeList_PageSize_Format");
            string fmt = (string.IsNullOrEmpty(rawFmt) || rawFmt == "Backtest_TradeList_PageSize_Format")
                ? "{0} per page"
                : rawFmt;
            optionsBuilder.Add(new TradePageSizeOption(size, string.Format(CultureInfo.CurrentCulture, fmt, size)));
        }
        TradePageSizeOptions = optionsBuilder.MoveToImmutable();
        _selectedTradePageSize = TradePageSizeOptions.FirstOrDefault(o => o.Value == selectedValue) ?? TradePageSizeOptions[0];
        OnPropertyChanged(nameof(TradePageSizeOptions));
        OnPropertyChanged(nameof(SelectedTradePageSize));
    }

    private void OnLocaleChanged(object? sender, EventArgs e)
    {
        RebuildTradePageSizeOptions();
        UpdatePagedTrades();
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LocalizationManager.Instance.LocaleChanged -= OnLocaleChanged;
        GC.SuppressFinalize(this);
    }

    #endregion
}
