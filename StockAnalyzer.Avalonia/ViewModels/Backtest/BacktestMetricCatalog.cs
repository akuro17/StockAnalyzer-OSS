using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Services.Backtest.Reporting;

namespace StockAnalyzer.Avalonia.ViewModels.Backtest;

/// <summary>Reading-order groups of the Results-tab metrics table (display order = declaration order in <see cref="BacktestMetricCatalog.Groups"/>).</summary>
internal enum BacktestMetricGroup { Performance, Drawdown, RiskAdjusted, TradeStatistics, TradeBehavior, RiskEfficiency }

internal enum BacktestMetricValueFormat { FourDecimals, TwoDecimals, WholeNumber, Percentage }

/// <summary>How a valid value is colored. Non-Valid values are always neutral, whatever the rule.</summary>
internal enum BacktestMetricSemanticRule
{
    /// <summary>By the sign of the value: only for metrics whose sign carries meaning (a result, an excess return, a ratio to a signed quantity).</summary>
    Sign,

    /// <summary>A non-negative gain magnitude: positive when above zero.</summary>
    GainMagnitude,

    /// <summary>A non-negative loss magnitude: negative (loss) color when above zero.</summary>
    LossMagnitude,

    /// <summary>The sign carries no meaning.</summary>
    Neutral,
}

/// <summary>One metric of the Results-tab table: where its value comes from, how it is formatted and colored, and which group shows it.</summary>
internal sealed record BacktestMetricDefinition
{
    public required string Name { get; init; }
    public required BacktestMetricGroup Group { get; init; }
    public required BacktestMetricValueFormat Format { get; init; }
    public required BacktestMetricSemanticRule Rule { get; init; }

    /// <summary>The report value; null when the report does not carry this metric (an extended metric of a report persisted before it existed).</summary>
    public required Func<BacktestReport, MetricValue?> Select { get; init; }

    /// <summary>True when every report, including one persisted before the extended metrics existed, carries this metric; only such a metric may appear in the result summary.</summary>
    public bool IsAlwaysPresent { get; init; }

    /// <summary>True when the row has a localized definition text (<c>Backtest_Metric_Tooltip_{Name}</c>).</summary>
    public bool HasDefinitionTooltip { get; init; }

    /// <summary>When set, the definition text is a format filled with these values (for example the trade counts).</summary>
    public Func<BacktestReport, object[]>? DefinitionArguments { get; init; }

    /// <summary>The format used when the localized definition text is missing or malformed; required together with <see cref="DefinitionArguments"/>.</summary>
    public string? DefinitionFallbackFormat { get; init; }

    /// <summary>For a drawdown duration: whether the value is only a lower bound (the episode had not recovered by the last bar).</summary>
    public Func<BacktestReport, bool>? IsRightCensored { get; init; }

    /// <summary>True for the row that shows the 95% confidence interval text next to its value.</summary>
    public bool ShowsConfidenceInterval { get; init; }

    /// <summary>The 1-based position of the metric in the result summary at the top of the Results tab (positions form an unbroken sequence from 1); null = not in the summary.</summary>
    public int? SummaryOrder { get; init; }
}

/// <summary>
/// Single definition table of the Results-tab metrics: display order, group, value source, format and color rule.
/// The order of <see cref="Definitions"/> is the display order; it is unrelated to the identity-hash order of
/// <see cref="BacktestReport.EnumerateExtendedMetrics"/>, which must never change.
/// </summary>
internal static class BacktestMetricCatalog
{
    private const string ClosedTradesName = "ClosedTrades";
    private const string GroupKeyPrefix = "Backtest_MetricGroup_";
    private const string ClosedTradesTooltipFallbackFormat = "{0} / {1} / {2}";

    /// <summary>Groups in display order.</summary>
    public static ImmutableArray<BacktestMetricGroup> Groups { get; } = ImmutableArray.Create(
        BacktestMetricGroup.Performance,
        BacktestMetricGroup.Drawdown,
        BacktestMetricGroup.RiskAdjusted,
        BacktestMetricGroup.TradeStatistics,
        BacktestMetricGroup.TradeBehavior,
        BacktestMetricGroup.RiskEfficiency);

    /// <summary>All metrics in display order (groups in <see cref="Groups"/> order, rows in declaration order inside a group).</summary>
    public static ImmutableArray<BacktestMetricDefinition> Definitions { get; } = BuildDefinitions();

    /// <summary>The metrics of the result summary in display order.</summary>
    public static ImmutableArray<BacktestMetricDefinition> SummaryDefinitions { get; } = Definitions
        .Where(definition => definition.SummaryOrder is not null)
        .OrderBy(definition => definition.SummaryOrder)
        .ToImmutableArray();

    public static string GroupKey(BacktestMetricGroup group) => GroupKeyPrefix + group;

    /// <summary>Closed world: a metric name that is not in the table is a defect, never silently dropped or guessed.</summary>
    public static BacktestMetricDefinition Get(string name) =>
        Definitions.FirstOrDefault(definition => string.Equals(definition.Name, name, StringComparison.Ordinal))
        ?? throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown metric name.");

    private static ImmutableArray<BacktestMetricDefinition> BuildDefinitions()
    {
        BacktestMetricDefinition[] table =
        {
            // Performance
            Fixed(nameof(BacktestReport.TotalPnL), BacktestMetricGroup.Performance, BacktestMetricValueFormat.FourDecimals, r => r.TotalPnL),
            Fixed(nameof(BacktestReport.TotalReturn), BacktestMetricGroup.Performance, BacktestMetricValueFormat.Percentage, r => r.TotalReturn) with { SummaryOrder = 1 },
            Fixed(nameof(BacktestReport.CAGR), BacktestMetricGroup.Performance, BacktestMetricValueFormat.Percentage, r => r.CAGR) with { SummaryOrder = 2 },
            Extended(nameof(BacktestReport.GrossProfit), BacktestMetricGroup.Performance, BacktestMetricValueFormat.FourDecimals, BacktestMetricSemanticRule.GainMagnitude, r => r.GrossProfit),
            Extended(nameof(BacktestReport.GrossLoss), BacktestMetricGroup.Performance, BacktestMetricValueFormat.FourDecimals, BacktestMetricSemanticRule.LossMagnitude, r => r.GrossLoss),

            // Drawdown
            Fixed(nameof(BacktestReport.MaxDrawdown), BacktestMetricGroup.Drawdown, BacktestMetricValueFormat.Percentage, r => r.MaxDrawdown, BacktestMetricSemanticRule.LossMagnitude) with { SummaryOrder = 3 },
            Fixed(nameof(BacktestReport.MaxDrawdownAmount), BacktestMetricGroup.Drawdown, BacktestMetricValueFormat.FourDecimals, r => r.MaxDrawdownAmount, BacktestMetricSemanticRule.LossMagnitude),
            Fixed(nameof(BacktestReport.UlcerIndex), BacktestMetricGroup.Drawdown, BacktestMetricValueFormat.FourDecimals, r => r.UlcerIndex, BacktestMetricSemanticRule.Neutral),
            Extended(nameof(BacktestReport.MaxDepthDrawdownDuration), BacktestMetricGroup.Drawdown, BacktestMetricValueFormat.WholeNumber, BacktestMetricSemanticRule.Neutral, r => r.MaxDepthDrawdownDuration) with
            {
                IsRightCensored = r => r.MaxDepthDrawdownDurationRightCensored == true,
            },
            Extended(nameof(BacktestReport.LongestDrawdownDuration), BacktestMetricGroup.Drawdown, BacktestMetricValueFormat.WholeNumber, BacktestMetricSemanticRule.Neutral, r => r.LongestDrawdownDuration) with
            {
                IsRightCensored = r => r.LongestDrawdownDurationRightCensored == true,
            },

            // Risk-adjusted
            Fixed(nameof(BacktestReport.AnnualizedSharpe), BacktestMetricGroup.RiskAdjusted, BacktestMetricValueFormat.FourDecimals, r => r.AnnualizedSharpe) with { SummaryOrder = 4 },
            Fixed(nameof(BacktestReport.AnnualizedSharpeAutocorrelationAdjusted), BacktestMetricGroup.RiskAdjusted, BacktestMetricValueFormat.FourDecimals, r => r.AnnualizedSharpeAutocorrelationAdjusted),
            Fixed(nameof(BacktestReport.AnnualizedSortino), BacktestMetricGroup.RiskAdjusted, BacktestMetricValueFormat.FourDecimals, r => r.AnnualizedSortino),
            Fixed(nameof(BacktestReport.AnnualizedSortinoAutocorrelationAdjusted), BacktestMetricGroup.RiskAdjusted, BacktestMetricValueFormat.FourDecimals, r => r.AnnualizedSortinoAutocorrelationAdjusted) with
            {
                ShowsConfidenceInterval = true,
            },
            Fixed(nameof(BacktestReport.CalmarFullPeriod), BacktestMetricGroup.RiskAdjusted, BacktestMetricValueFormat.FourDecimals, r => r.CalmarFullPeriod),
            Fixed(nameof(BacktestReport.RecoveryFactor), BacktestMetricGroup.RiskAdjusted, BacktestMetricValueFormat.FourDecimals, r => r.RecoveryFactor),

            // Trade statistics
            new BacktestMetricDefinition
            {
                Name = ClosedTradesName,
                Group = BacktestMetricGroup.TradeStatistics,
                Format = BacktestMetricValueFormat.WholeNumber,
                Rule = BacktestMetricSemanticRule.Neutral,
                Select = r => MetricValue.Valid(r.TotalTrades, MetricUnit.Count),
                IsAlwaysPresent = true,
                HasDefinitionTooltip = true,
                DefinitionArguments = r => new object[] { r.WinTrades, r.LossTrades, r.BreakevenTrades },
                DefinitionFallbackFormat = ClosedTradesTooltipFallbackFormat,
                SummaryOrder = 5,
            },
            Fixed(nameof(BacktestReport.WinRate), BacktestMetricGroup.TradeStatistics, BacktestMetricValueFormat.Percentage, r => r.WinRate, BacktestMetricSemanticRule.Neutral),
            Fixed(nameof(BacktestReport.ProfitFactor), BacktestMetricGroup.TradeStatistics, BacktestMetricValueFormat.FourDecimals, r => r.ProfitFactor, BacktestMetricSemanticRule.Neutral),
            Fixed(nameof(BacktestReport.ExpectedPayoff), BacktestMetricGroup.TradeStatistics, BacktestMetricValueFormat.FourDecimals, r => r.ExpectedPayoff),
            Extended(nameof(BacktestReport.PayoffRatio), BacktestMetricGroup.TradeStatistics, BacktestMetricValueFormat.FourDecimals, BacktestMetricSemanticRule.Neutral, r => r.PayoffRatio),
            Extended(nameof(BacktestReport.AverageWin), BacktestMetricGroup.TradeStatistics, BacktestMetricValueFormat.FourDecimals, BacktestMetricSemanticRule.GainMagnitude, r => r.AverageWin),
            Extended(nameof(BacktestReport.AverageLoss), BacktestMetricGroup.TradeStatistics, BacktestMetricValueFormat.FourDecimals, BacktestMetricSemanticRule.LossMagnitude, r => r.AverageLoss),
            Extended(nameof(BacktestReport.LargestWin), BacktestMetricGroup.TradeStatistics, BacktestMetricValueFormat.FourDecimals, BacktestMetricSemanticRule.GainMagnitude, r => r.LargestWin),
            Extended(nameof(BacktestReport.LargestLoss), BacktestMetricGroup.TradeStatistics, BacktestMetricValueFormat.FourDecimals, BacktestMetricSemanticRule.LossMagnitude, r => r.LargestLoss),

            // Trade behavior
            Extended(nameof(BacktestReport.AverageHoldingPeriod), BacktestMetricGroup.TradeBehavior, BacktestMetricValueFormat.TwoDecimals, BacktestMetricSemanticRule.Neutral, r => r.AverageHoldingPeriod),
            Extended(nameof(BacktestReport.MaxConsecutiveWins), BacktestMetricGroup.TradeBehavior, BacktestMetricValueFormat.WholeNumber, BacktestMetricSemanticRule.Neutral, r => r.MaxConsecutiveWins),
            Extended(nameof(BacktestReport.MaxConsecutiveLosses), BacktestMetricGroup.TradeBehavior, BacktestMetricValueFormat.WholeNumber, BacktestMetricSemanticRule.Neutral, r => r.MaxConsecutiveLosses),
            Extended(nameof(BacktestReport.TimeInMarket), BacktestMetricGroup.TradeBehavior, BacktestMetricValueFormat.WholeNumber, BacktestMetricSemanticRule.Neutral, r => r.TimeInMarket),
            Extended(nameof(BacktestReport.Exposure), BacktestMetricGroup.TradeBehavior, BacktestMetricValueFormat.Percentage, BacktestMetricSemanticRule.Neutral, r => r.Exposure),

            // Risk & efficiency
            Fixed(nameof(BacktestReport.BarSharpe), BacktestMetricGroup.RiskEfficiency, BacktestMetricValueFormat.FourDecimals, r => r.BarSharpe),
            Fixed(nameof(BacktestReport.BarSortino), BacktestMetricGroup.RiskEfficiency, BacktestMetricValueFormat.FourDecimals, r => r.BarSortino),
            Extended(nameof(BacktestReport.ExposureAdjustedCAGR), BacktestMetricGroup.RiskEfficiency, BacktestMetricValueFormat.Percentage, BacktestMetricSemanticRule.Sign, r => r.ExposureAdjustedCAGR),
            Fixed(nameof(BacktestReport.SQN), BacktestMetricGroup.RiskEfficiency, BacktestMetricValueFormat.FourDecimals, r => r.SQN, BacktestMetricSemanticRule.Neutral),
        };

        Validate(table, Groups);

        // Display order is group order first, then declaration order inside a group.
        return Groups.SelectMany(group => table.Where(definition => definition.Group == group)).ToImmutableArray();
    }

    /// <summary>
    /// Closed-world check of a definition table, run once when the catalog is built so that no inconsistency can reach the screen:
    /// every group is listed (and listed once), every definition belongs to a listed group, names and summary positions are unique,
    /// and every format and color rule is a defined kind.
    /// </summary>
    internal static void Validate(IReadOnlyCollection<BacktestMetricDefinition> table, IReadOnlyCollection<BacktestMetricGroup> groups)
    {
        BacktestMetricGroup[] allGroups = Enum.GetValues<BacktestMetricGroup>();
        if (groups.Distinct().Count() != groups.Count)
        {
            throw new InvalidOperationException("A metric group is listed more than once in the group order.");
        }

        BacktestMetricGroup[] missing = allGroups.Except(groups).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Metric groups missing from the group order (their rows would never be shown): {string.Join(", ", missing)}.");
        }

        foreach (BacktestMetricDefinition definition in table)
        {
            if (!Enum.IsDefined(definition.Format))
            {
                throw new ArgumentOutOfRangeException(nameof(BacktestMetricDefinition.Format), definition.Format, $"Undefined metric value format of '{definition.Name}'.");
            }

            if (!Enum.IsDefined(definition.Rule))
            {
                throw new ArgumentOutOfRangeException(nameof(BacktestMetricDefinition.Rule), definition.Rule, $"Undefined metric semantic rule of '{definition.Name}'.");
            }

            if (!groups.Contains(definition.Group))
            {
                throw new InvalidOperationException($"The group '{definition.Group}' of the metric '{definition.Name}' is not in the group order.");
            }
        }

        string? duplicateName = table.GroupBy(definition => definition.Name, StringComparer.Ordinal).FirstOrDefault(entry => entry.Count() > 1)?.Key;
        if (duplicateName is not null)
        {
            throw new InvalidOperationException($"The metric name '{duplicateName}' is defined more than once.");
        }

        foreach (BacktestMetricDefinition definition in table)
        {
            // The summary of a report that lacks the metric would fail to build, so only an always-present metric may be shown there.
            if (definition.SummaryOrder is not null && !definition.IsAlwaysPresent)
            {
                throw new InvalidOperationException($"The metric '{definition.Name}' is in the summary but a report may not carry it.");
            }

            // A formatted definition text needs both its arguments and its fallback format, and only a row with a definition text can use them.
            if ((definition.DefinitionArguments is null) != (definition.DefinitionFallbackFormat is null)
                || (definition.DefinitionArguments is not null && !definition.HasDefinitionTooltip))
            {
                throw new InvalidOperationException($"The definition text of the metric '{definition.Name}' needs arguments, a fallback format and a definition tooltip together.");
            }
        }

        int[] summaryPositions = table.Where(definition => definition.SummaryOrder is not null).Select(definition => definition.SummaryOrder!.Value).OrderBy(position => position).ToArray();
        if (!summaryPositions.SequenceEqual(Enumerable.Range(1, summaryPositions.Length)))
        {
            throw new InvalidOperationException($"The summary positions must be unique and form an unbroken sequence from 1 (found: {string.Join(", ", summaryPositions)}).");
        }
    }

    /// <summary>A metric of the original fixed table: always present, no definition text yet; colored by sign unless a rule is given.</summary>
    private static BacktestMetricDefinition Fixed(
        string name,
        BacktestMetricGroup group,
        BacktestMetricValueFormat format,
        Func<BacktestReport, MetricValue> select,
        BacktestMetricSemanticRule rule = BacktestMetricSemanticRule.Sign) =>
        new()
        {
            Name = name,
            Group = group,
            Format = format,
            Rule = rule,
            Select = report => select(report),
            IsAlwaysPresent = true,
        };

    /// <summary>An extended metric: may be absent on a legacy report, always has a localized definition text.</summary>
    private static BacktestMetricDefinition Extended(
        string name,
        BacktestMetricGroup group,
        BacktestMetricValueFormat format,
        BacktestMetricSemanticRule rule,
        Func<BacktestReport, MetricValue?> select) =>
        new()
        {
            Name = name,
            Group = group,
            Format = format,
            Rule = rule,
            Select = select,
            HasDefinitionTooltip = true,
        };
}
