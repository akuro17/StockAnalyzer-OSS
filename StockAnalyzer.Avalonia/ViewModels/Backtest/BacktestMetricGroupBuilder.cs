using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>One row of the grouped Results-tab metrics table. One type for every metric, fixed or extended.</summary>
public sealed record BacktestMetricDisplayRow
{
    public required string LabelKey { get; init; }
    public required string ValueText { get; init; }

    /// <summary>Null when there is nothing to say (no tooltip is shown).</summary>
    public string? TooltipText { get; init; }

    public required MetricStatus Status { get; init; }
    public required BacktestMetricSemantic Semantic { get; init; }

    /// <summary>True when a drawdown-duration value is a lower bound (the episode had not recovered by the last bar).</summary>
    public required bool IsLowerBound { get; init; }

    /// <summary>The 95% confidence interval text of the row that has one; null for every other row.</summary>
    public string? ConfidenceIntervalText { get; init; }
}

/// <summary>A titled group of metric rows; groups without rows are never produced.</summary>
public sealed record BacktestMetricGroupPresentation
{
    public required string GroupKey { get; init; }
    public required ImmutableArray<BacktestMetricDisplayRow> Rows { get; init; }
}

/// <summary>
/// Pure builder of the grouped metrics table from <see cref="BacktestMetricCatalog"/>. It formats computed values only
/// and never recomputes a metric; the row set, order, formats and colors come from the catalog alone.
/// </summary>
internal static class BacktestMetricGroupBuilder
{
    private const string LabelKeyPrefix = "Backtest_Metric_";
    private const string TooltipKeyPrefix = "Backtest_Metric_Tooltip_";
    private const string StatusKeyPrefix = "Backtest_Metric_Status_";
    private const string ReasonKeyPrefix = "Backtest_Metric_Reason_";
    private const string NotAvailableKey = "Backtest_Metric_NotAvailable";
    private const string LowerBoundTemplateKey = "Backtest_Metric_LowerBoundTemplate";
    private const string LowerBoundTooltipKey = "Backtest_Metric_Tooltip_LowerBound";
    private const string NonValidTooltipKey = "Backtest_Metric_Tooltip_NonValid";
    private const string LowerBoundFallbackFormat = "≥ {0}";
    private const string NonValidTooltipFallbackFormat = "{0} ({1})";
    private const string TooltipLineSeparator = "\n";
    private const string ConfidenceIntervalFormat = "[{0}, {1}]";

    private const string FourDecimalsFormat = "F4";
    private const string TwoDecimalsFormat = "F2";
    private const string WholeNumberFormat = "F0";

    public static ImmutableArray<BacktestMetricGroupPresentation> Build(
        BacktestReport report,
        ILocalizationService localization,
        IReadOnlySet<string>? visibleMetricNames = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localization);

        // An extended metric the catalog does not know must fail loudly, not vanish from the screen.
        foreach ((string name, _, _) in report.EnumerateExtendedMetrics())
        {
            BacktestMetricCatalog.Get(name);
        }

        ImmutableArray<BacktestMetricGroupPresentation>.Builder groups = ImmutableArray.CreateBuilder<BacktestMetricGroupPresentation>();
        foreach (BacktestMetricGroup group in BacktestMetricCatalog.Groups)
        {
            ImmutableArray<BacktestMetricDisplayRow>.Builder rows = ImmutableArray.CreateBuilder<BacktestMetricDisplayRow>();
            foreach (BacktestMetricDefinition definition in BacktestMetricCatalog.Definitions)
            {
                if (definition.Group != group) continue;
                if (visibleMetricNames != null && !visibleMetricNames.Contains(definition.Name)) continue;
                if (definition.Select(report) is not { } metric) continue;
                rows.Add(BuildRow(definition, metric, report, localization));
            }

            if (rows.Count == 0) continue;
            groups.Add(new BacktestMetricGroupPresentation
            {
                GroupKey = BacktestMetricCatalog.GroupKey(group),
                Rows = rows.ToImmutable(),
            });
        }

        return groups.ToImmutable();
    }

    /// <summary>
    /// Builds placeholder metric groups for pre-run state: all catalog groups and definitions are populated
    /// with placeholder value ("-") in Neutral semantic, allowing users to see all available metric items
    /// and their definitions before executing a run.
    /// </summary>
    public static ImmutableArray<BacktestMetricGroupPresentation> BuildPlaceholder(
        ILocalizationService localization,
        IReadOnlySet<string>? visibleMetricNames = null)
    {
        ArgumentNullException.ThrowIfNull(localization);

        ImmutableArray<BacktestMetricGroupPresentation>.Builder groups = ImmutableArray.CreateBuilder<BacktestMetricGroupPresentation>();
        foreach (BacktestMetricGroup group in BacktestMetricCatalog.Groups)
        {
            ImmutableArray<BacktestMetricDisplayRow>.Builder rows = ImmutableArray.CreateBuilder<BacktestMetricDisplayRow>();
            foreach (BacktestMetricDefinition definition in BacktestMetricCatalog.Definitions)
            {
                if (definition.Group != group) continue;
                if (visibleMetricNames != null && !visibleMetricNames.Contains(definition.Name)) continue;
                string? definitionText = definition.HasDefinitionTooltip ? localization.GetString(TooltipKeyPrefix + definition.Name) : null;
                rows.Add(new BacktestMetricDisplayRow
                {
                    LabelKey = LabelKeyPrefix + definition.Name,
                    ValueText = "-",
                    TooltipText = definitionText,
                    Status = MetricStatus.NotApplicable,
                    Semantic = BacktestMetricSemantic.Neutral,
                    IsLowerBound = false,
                    ConfidenceIntervalText = null,
                });
            }

            if (rows.Count == 0) continue;
            groups.Add(new BacktestMetricGroupPresentation
            {
                GroupKey = BacktestMetricCatalog.GroupKey(group),
                Rows = rows.ToImmutable(),
            });
        }

        return groups.ToImmutable();
    }

    /// <summary>
    /// Filters the given metric groups to include only rows whose metric names are present in <paramref name="visibleMetricNames"/>,
    /// retaining the exact same row object instances as <paramref name="groups"/> and omitting empty groups.
    /// When <paramref name="visibleMetricNames"/> is ordered, groups and rows are ordered according to the sequence in <paramref name="visibleMetricNames"/>.
    /// </summary>
    public static ImmutableArray<BacktestMetricGroupPresentation> FilterGroups(
        ImmutableArray<BacktestMetricGroupPresentation> groups,
        IEnumerable<string>? visibleMetricNames)
    {
        if (visibleMetricNames == null) return groups;

        var rowMap = new Dictionary<string, (string GroupKey, BacktestMetricDisplayRow Row)>(StringComparer.OrdinalIgnoreCase);
        foreach (BacktestMetricGroupPresentation group in groups)
        {
            foreach (BacktestMetricDisplayRow row in group.Rows)
            {
                if (row.LabelKey.StartsWith(LabelKeyPrefix, StringComparison.Ordinal))
                {
                    string metricName = row.LabelKey.Substring(LabelKeyPrefix.Length);
                    rowMap[metricName] = (group.GroupKey, row);
                }
            }
        }

        var groupRows = new Dictionary<string, List<BacktestMetricDisplayRow>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();
        var seenMetrics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in visibleMetricNames)
        {
            if (seenMetrics.Add(name) && rowMap.TryGetValue(name, out var item))
            {
                if (!groupRows.TryGetValue(item.GroupKey, out var list))
                {
                    list = new List<BacktestMetricDisplayRow>();
                    groupRows[item.GroupKey] = list;
                    groupOrder.Add(item.GroupKey);
                }
                list.Add(item.Row);
            }
        }

        ImmutableArray<BacktestMetricGroupPresentation>.Builder result = ImmutableArray.CreateBuilder<BacktestMetricGroupPresentation>();
        foreach (string groupKey in groupOrder)
        {
            result.Add(new BacktestMetricGroupPresentation
            {
                GroupKey = groupKey,
                Rows = groupRows[groupKey].ToImmutableArray(),
            });
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// The result summary: the catalog's summary metrics in summary order. Each item is the very row object of <paramref name="groups"/>,
    /// so a summary value can never differ from the table.
    /// </summary>
    public static ImmutableArray<BacktestMetricDisplayRow> BuildSummary(ImmutableArray<BacktestMetricGroupPresentation> groups) =>
        SelectRows(groups, BacktestMetricCatalog.SummaryDefinitions.Select(definition => definition.Name));

    /// <summary>Picks the rows of the given metric names, in that order; a name without a row is a catalog inconsistency and throws.</summary>
    internal static ImmutableArray<BacktestMetricDisplayRow> SelectRows(
        ImmutableArray<BacktestMetricGroupPresentation> groups, IEnumerable<string> names)
    {
        ImmutableArray<BacktestMetricDisplayRow>.Builder rows = ImmutableArray.CreateBuilder<BacktestMetricDisplayRow>();
        foreach (string name in names)
        {
            string labelKey = LabelKeyPrefix + name;
            BacktestMetricDisplayRow? row = groups.SelectMany(group => group.Rows)
                .FirstOrDefault(candidate => string.Equals(candidate.LabelKey, labelKey, StringComparison.Ordinal));
            rows.Add(row ?? throw new InvalidOperationException($"The summary metric '{name}' has no row in the metrics table."));
        }

        return rows.ToImmutable();
    }

    /// <summary>Formats a value by its format kind (closed world: an undefined kind throws).</summary>
    internal static string FormatValue(BacktestMetricValueFormat format, decimal value) => format switch
    {
        BacktestMetricValueFormat.FourDecimals => value.ToString(FourDecimalsFormat, CultureInfo.CurrentCulture),
        BacktestMetricValueFormat.TwoDecimals => value.ToString(TwoDecimalsFormat, CultureInfo.CurrentCulture),
        BacktestMetricValueFormat.WholeNumber => value.ToString(WholeNumberFormat, CultureInfo.CurrentCulture),
        BacktestMetricValueFormat.Percentage => BacktestMetricFormatter.FormatPercentage(value),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown metric value format."),
    };

    /// <summary>The single sign rule of the Results tab: above zero is Plus, below zero is Minus, zero is Neutral.</summary>
    internal static BacktestMetricSemantic ResolveBySign(decimal value) =>
        value > 0m ? BacktestMetricSemantic.Plus : value < 0m ? BacktestMetricSemantic.Minus : BacktestMetricSemantic.Neutral;

    /// <summary>Colors a valid value by its rule (closed world: an undefined rule throws).</summary>
    internal static BacktestMetricSemantic ResolveSemantic(BacktestMetricSemanticRule rule, decimal value) => rule switch
    {
        BacktestMetricSemanticRule.Sign => ResolveBySign(value),
        BacktestMetricSemanticRule.GainMagnitude => value > 0m ? BacktestMetricSemantic.Plus : BacktestMetricSemantic.Neutral,
        BacktestMetricSemanticRule.LossMagnitude => value > 0m ? BacktestMetricSemantic.Minus : BacktestMetricSemantic.Neutral,
        BacktestMetricSemanticRule.Neutral => BacktestMetricSemantic.Neutral,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown metric semantic rule."),
    };

    private static BacktestMetricDisplayRow BuildRow(
        BacktestMetricDefinition definition, MetricValue metric, BacktestReport report, ILocalizationService localization)
    {
        string? definitionText = BuildDefinitionText(definition, report, localization);
        string? confidenceInterval = definition.ShowsConfidenceInterval ? BuildConfidenceIntervalText(report, localization) : null;

        string? valueText = null;
        if (metric.Status != MetricStatus.Valid || metric.Value is not { } value)
        {
            return new BacktestMetricDisplayRow
            {
                LabelKey = LabelKeyPrefix + definition.Name,
                ValueText = localization.GetString(NotAvailableKey),
                TooltipText = Join(definitionText, BuildNonValidSentence(metric, localization)),
                Status = metric.Status,
                Semantic = BacktestMetricSemantic.Neutral,
                IsLowerBound = false,
                ConfidenceIntervalText = confidenceInterval,
            };
        }

        bool isLowerBound = definition.IsRightCensored?.Invoke(report) == true;
        valueText = FormatValue(definition.Format, value);
        string? tooltip = definitionText;
        if (isLowerBound)
        {
            valueText = localization.GetFormattedString(LowerBoundTemplateKey, LowerBoundFallbackFormat, valueText);
            tooltip = Join(tooltip, localization.GetString(LowerBoundTooltipKey));
        }

        if (report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval is not null && !string.IsNullOrEmpty(confidenceInterval))
        {
            valueText = $"{valueText} {confidenceInterval}";
        }

        return new BacktestMetricDisplayRow
        {
            LabelKey = LabelKeyPrefix + definition.Name,
            ValueText = valueText,
            TooltipText = tooltip,
            Status = metric.Status,
            Semantic = ResolveSemantic(definition.Rule, value),
            IsLowerBound = isLowerBound,
            ConfidenceIntervalText = confidenceInterval,
        };
    }

    private static string? BuildDefinitionText(BacktestMetricDefinition definition, BacktestReport report, ILocalizationService localization)
    {
        if (!definition.HasDefinitionTooltip) return null;

        string key = TooltipKeyPrefix + definition.Name;
        return definition.DefinitionArguments is null
            ? localization.GetString(key)
            : localization.GetFormattedString(
                key,
                definition.DefinitionFallbackFormat ?? throw new InvalidOperationException("A formatted definition needs a fallback format."),
                definition.DefinitionArguments(report));
    }

    private static string BuildConfidenceIntervalText(BacktestReport report, ILocalizationService localization) =>
        report.AnnualizedSortinoAutocorrelationAdjustedConfidenceInterval is { } interval
            ? string.Format(
                CultureInfo.CurrentCulture,
                ConfidenceIntervalFormat,
                interval.Lower.ToString(FourDecimalsFormat, CultureInfo.CurrentCulture),
                interval.Upper.ToString(FourDecimalsFormat, CultureInfo.CurrentCulture))
            : localization.GetString(NotAvailableKey);

    private static string BuildNonValidSentence(MetricValue metric, ILocalizationService localization)
    {
        string statusName = localization.GetString(StatusKeyPrefix + metric.Status);
        return metric.Reason == MetricReason.None
            ? statusName
            : localization.GetFormattedString(
                NonValidTooltipKey,
                NonValidTooltipFallbackFormat,
                statusName,
                localization.GetString(ReasonKeyPrefix + metric.Reason));
    }

    private static string Join(string? first, string second) =>
        string.IsNullOrEmpty(first) ? second : first + TooltipLineSeparator + second;
}
