using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;
using static StockAnalyzer.Tests.Backtest.BacktestReportTestFactory;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Grouped Results-tab metrics table (Y:\Temp\sa_implementation_plan_BacktestMetricsGroupedLayout.md): real generated reports, no mocks.</summary>
public class BacktestMetricGroupBuilderTests
{
    private static readonly string[] ExpectedGroupKeys =
    {
        "Backtest_MetricGroup_Performance",
        "Backtest_MetricGroup_Drawdown",
        "Backtest_MetricGroup_RiskAdjusted",
        "Backtest_MetricGroup_TradeStatistics",
        "Backtest_MetricGroup_TradeBehavior",
        "Backtest_MetricGroup_RiskEfficiency",
    };

    private static readonly string[][] ExpectedRowNames =
    {
        new[] { "TotalPnL", "TotalReturn", "CAGR", "GrossProfit", "GrossLoss" },
        new[] { "MaxDrawdown", "MaxDrawdownAmount", "UlcerIndex", "MaxDepthDrawdownDuration", "LongestDrawdownDuration" },
        new[]
        {
            "AnnualizedSharpe", "AnnualizedSharpeAutocorrelationAdjusted", "AnnualizedSortino",
            "AnnualizedSortinoAutocorrelationAdjusted", "CalmarFullPeriod", "RecoveryFactor",
        },
        new[]
        {
            "ClosedTrades", "WinRate", "ProfitFactor", "ExpectedPayoff", "PayoffRatio",
            "AverageWin", "AverageLoss", "LargestWin", "LargestLoss",
        },
        new[] { "AverageHoldingPeriod", "MaxConsecutiveWins", "MaxConsecutiveLosses", "TimeInMarket", "Exposure" },
        new[] { "BarSharpe", "BarSortino", "ExposureAdjustedCAGR", "SQN" },
    };

    private static readonly string[] FixedMetricNames =
    {
        "TotalPnL", "MaxDrawdownAmount", "ExpectedPayoff", "TotalReturn", "CAGR", "WinRate", "ProfitFactor", "MaxDrawdown",
        "UlcerIndex", "BarSharpe", "AnnualizedSharpe", "AnnualizedSharpeAutocorrelationAdjusted", "BarSortino",
        "AnnualizedSortino", "AnnualizedSortinoAutocorrelationAdjusted", "CalmarFullPeriod", "SQN", "RecoveryFactor",
    };

    /// <summary>Every row of a generated report: the sum of the expected per-group tables.</summary>
    private static readonly int ExpectedTotalRowCount = ExpectedRowNames.Sum(names => names.Length);

    /// <summary>A report persisted before the extended metrics existed shows the 18 fixed metrics and Closed Trades only.</summary>
    private static readonly int LegacyTotalRowCount = FixedMetricNames.Length + 1;

    private static string[] RowNames(BacktestMetricGroupPresentation group) =>
        group.Rows.Select(row => row.LabelKey["Backtest_Metric_".Length..]).ToArray();

    // ---- G1 -------------------------------------------------------------------------------------

    [Fact]
    public void Build_GeneratedReport_ReturnsSixGroupsInReadingOrder()
    {
        BacktestReport report = GenerateReport(10m, -4m, 6m, -2m);

        ImmutableArray<BacktestMetricGroupPresentation> groups = BacktestMetricGroupBuilder.Build(report, NullLocalizationService.Instance);

        Assert.Equal(ExpectedGroupKeys, groups.Select(g => g.GroupKey));
        Assert.Equal(new[] { 5, 5, 6, 9, 5, 4 }, groups.Select(g => g.Rows.Length));
        Assert.Equal(ExpectedTotalRowCount, groups.Sum(g => g.Rows.Length));
        for (int i = 0; i < groups.Length; i++)
        {
            Assert.Equal(ExpectedRowNames[i], RowNames(groups[i]));
        }
    }

    // ---- G2 -------------------------------------------------------------------------------------

    [Fact]
    public void Build_EveryExistingMetricAppearsExactlyOnce()
    {
        BacktestReport report = GenerateReport(10m, -4m, 6m, -2m);

        string[] shown = BacktestMetricGroupBuilder.Build(report, NullLocalizationService.Instance)
            .SelectMany(g => g.Rows)
            .Select(row => row.LabelKey)
            .ToArray();

        string[] expected = FixedMetricNames
            .Concat(report.EnumerateExtendedMetrics().Select(m => m.Name))
            .Append("ClosedTrades")
            .Select(name => "Backtest_Metric_" + name)
            .ToArray();
        Assert.Equal(ExpectedTotalRowCount, shown.Length);
        Assert.Equal(expected.OrderBy(n => n, StringComparer.Ordinal), shown.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(shown.Length, shown.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Build_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => BacktestMetricGroupBuilder.Build(null!, NullLocalizationService.Instance));
        Assert.Throws<ArgumentNullException>(() => BacktestMetricGroupBuilder.Build(BacktestTestFactory.CreateStubReport(), null!));
    }

    // ---- G5 -------------------------------------------------------------------------------------

    [Fact]
    public void Build_ClosedTrades_ShowsTheTradeCountAndBreakdownInTheTooltip()
    {
        var locale = new FakeLocalizationService(new System.Collections.Generic.Dictionary<string, string>
        {
            ["Backtest_Metric_Tooltip_ClosedTrades"] = "Number of closed trades. Winning {0} / Losing {1} / Breakeven {2}.",
        });
        BacktestReport report = GenerateReport(10m, -4m, 6m, 0m);

        BacktestMetricDisplayRow row = BacktestMetricGroupBuilder.Build(report, NullLocalizationService.Instance)
            .SelectMany(g => g.Rows)
            .Single(r => r.LabelKey == "Backtest_Metric_ClosedTrades");

        Assert.Equal(report.TotalTrades.ToString("F0", System.Globalization.CultureInfo.CurrentCulture), row.ValueText);
        Assert.Equal(MetricStatus.Valid, row.Status);
        Assert.Equal(BacktestMetricSemantic.Neutral, row.Semantic);
        Assert.False(row.IsLowerBound);
        Assert.Null(row.ConfidenceIntervalText);

        // The tooltip carries the win / loss / breakeven counts of the report.
        string tooltip = BacktestMetricGroupBuilder.Build(report, locale)
            .SelectMany(g => g.Rows)
            .Single(r => r.LabelKey == "Backtest_Metric_ClosedTrades")
            .TooltipText!;
        Assert.Contains($"Winning {report.WinTrades} / Losing {report.LossTrades} / Breakeven {report.BreakevenTrades}", tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_LegacyReport_StillShowsClosedTrades()
    {
        BacktestReport legacy = BacktestTestFactory.CreateStubReport();

        BacktestMetricDisplayRow row = BacktestMetricGroupBuilder.Build(legacy, NullLocalizationService.Instance)
            .SelectMany(g => g.Rows)
            .Single(r => r.LabelKey == "Backtest_Metric_ClosedTrades");

        Assert.Equal("0", row.ValueText);
    }

    [Fact]
    public void Build_ClosedTrades_UsesTheNeutralFallbackWhenTheLocaleTextIsEmpty()
    {
        var locale = new FakeLocalizationService(new System.Collections.Generic.Dictionary<string, string>
        {
            ["Backtest_Metric_Tooltip_ClosedTrades"] = string.Empty,
        });
        BacktestReport report = GenerateReport(10m, -4m, 6m, 0m);

        string tooltip = BacktestMetricGroupBuilder.Build(report, locale)
            .SelectMany(g => g.Rows)
            .Single(r => r.LabelKey == "Backtest_Metric_ClosedTrades")
            .TooltipText!;

        Assert.Equal($"{report.WinTrades} / {report.LossTrades} / {report.BreakevenTrades}", tooltip);
    }

    // ---- G6 (builder part) ----------------------------------------------------------------------

    [Fact]
    public void Build_LegacyReport_HasNineteenRowsAndNoEmptyGroup()
    {
        BacktestReport legacy = BacktestTestFactory.CreateStubReport();
        Assert.Empty(legacy.EnumerateExtendedMetrics());

        ImmutableArray<BacktestMetricGroupPresentation> groups = BacktestMetricGroupBuilder.Build(legacy, NullLocalizationService.Instance);

        Assert.Equal(LegacyTotalRowCount, groups.Sum(g => g.Rows.Length));
        Assert.All(groups, g => Assert.NotEmpty(g.Rows));
        Assert.DoesNotContain(groups, g => g.GroupKey == "Backtest_MetricGroup_TradeBehavior");
        BacktestMetricGroupPresentation riskEfficiency = groups.Single(g => g.GroupKey == "Backtest_MetricGroup_RiskEfficiency");
        Assert.Equal(new[] { "BarSharpe", "BarSortino", "SQN" }, RowNames(riskEfficiency));
    }

    // ---- G6 / G8 (need the view model) ------------------------------------------------

    private static BacktestResultsViewModel CreateViewModel(ILocalizationService localization) =>
        new(localization, new FakeBacktestReportExporter(), new FakeDialogService());

    [Fact]
    public void Update_PublishesGroupsAndReplacesThemOnTheNextUpdate()
    {
        BacktestResultsViewModel vm = CreateViewModel(NullLocalizationService.Instance);
        var raised = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(vm.HasMetricGroups);
        Assert.Equal(0, vm.MetricGroups.Length);

        vm.Update(BacktestTestFactory.CreateResult(), GenerateReport(10m, -4m, 6m, -2m), 0);

        Assert.Equal(6, vm.MetricGroups.Length);
        Assert.True(vm.HasMetricGroups);
        Assert.Contains(nameof(BacktestResultsViewModel.MetricGroups), raised);
        Assert.Contains(nameof(BacktestResultsViewModel.HasMetricGroups), raised);
        ImmutableArray<BacktestMetricGroupPresentation> first = vm.MetricGroups;

        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0);

        Assert.NotSame(first.Single(g => g.GroupKey == "Backtest_MetricGroup_RiskEfficiency").Rows, vm.MetricGroups.Single(g => g.GroupKey == "Backtest_MetricGroup_RiskEfficiency").Rows);
        Assert.Equal(LegacyTotalRowCount, vm.MetricGroups.Sum(g => g.Rows.Length));
    }

    [Fact]
    public void Update_LegacyReport_PublishesGroupsWithoutTheExtendedRows()
    {
        BacktestResultsViewModel vm = CreateViewModel(NullLocalizationService.Instance);

        vm.Update(BacktestTestFactory.CreateResult(), BacktestTestFactory.CreateStubReport(), 0);

        Assert.Equal(LegacyTotalRowCount, vm.MetricGroups.Sum(g => g.Rows.Length));
        Assert.DoesNotContain(vm.MetricGroups, g => g.GroupKey == "Backtest_MetricGroup_TradeBehavior");
    }

    // ---- G9 -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void Locale_ContainsEveryGroupKeyAndTheClosedTradesKeys(string languageCode)
    {
        string path = System.IO.Path.Combine(RepoPaths.Root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{languageCode}.json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
        System.Text.Json.JsonElement root = document.RootElement;

        string[] required = ExpectedGroupKeys
            .Concat(new[] { "Backtest_Metric_ClosedTrades", "Backtest_Metric_Tooltip_ClosedTrades" })
            .ToArray();
        string[] missing = required
            .Where(key => !root.TryGetProperty(key, out System.Text.Json.JsonElement value)
                || value.ValueKind != System.Text.Json.JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            .ToArray();

        Assert.True(missing.Length == 0, $"{languageCode}.json is missing or has empty values for: {string.Join(", ", missing)}");
    }

    /// <summary>Attributes whose literal value is text a user reads or a screen reader announces. A StringFormat carries punctuation only and AutomationId is an identifier, so neither is text.</summary>
    private static readonly string[] VisibleTextAttributes =
    {
        "Text", "Content", "Header", "Title", "Label", "Watermark", "PlaceholderText", "ToolTip.Tip",
        "AutomationProperties.Name", "AutomationProperties.HelpText",
    };

    private static string ReadResultsViewAxaml() =>
        System.IO.File.ReadAllText(System.IO.Path.Combine(RepoPaths.Root, "StockAnalyzer.Avalonia", "Views", "Backtest", "BacktestResultsView.axaml"));

    [Fact]
    public void ResultsView_HasNoLiteralVisibleText()
    {
        // Localization Audit Requirement: the view of a modified feature carries no hard-coded user-visible text.
        string pattern = $"\\b(?:{string.Join("|", VisibleTextAttributes.Select(System.Text.RegularExpressions.Regex.Escape))})=\"(?!\\{{)[^\"]+\"";
        string[] literals = System.Text.RegularExpressions.Regex.Matches(ReadResultsViewAxaml(), pattern).Select(match => match.Value).ToArray();

        Assert.True(literals.Length == 0, $"Literal visible text in BacktestResultsView.axaml: {string.Join(", ", literals)}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void ResultsView_EveryLocalizedKeyExists(string languageCode)
    {
        string axaml = ReadResultsViewAxaml();
        string path = System.IO.Path.Combine(RepoPaths.Root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{languageCode}.json");
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(path));
        string[] missing = System.Text.RegularExpressions.Regex
            .Matches(axaml, "\\{l:Localize\\s+(?<key>[A-Za-z0-9_]+)\\}")
            .Select(match => match.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .Where(key => !document.RootElement.TryGetProperty(key, out System.Text.Json.JsonElement value)
                || value.ValueKind != System.Text.Json.JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            .ToArray();

        Assert.True(missing.Length == 0, $"{languageCode}.json lacks keys used by BacktestResultsView.axaml: {string.Join(", ", missing)}");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void Locale_ClosedTradesTooltipKeepsItsThreePlaceholders(string languageCode)
    {
        FakeLocalizationService locale = LoadLocale(languageCode);
        BacktestReport report = GenerateReport(10m, -4m, 6m, 0m);

        string tooltip = BacktestMetricGroupBuilder.Build(report, locale)
            .SelectMany(g => g.Rows)
            .Single(r => r.LabelKey == "Backtest_Metric_ClosedTrades")
            .TooltipText!;

        Assert.DoesNotContain("{", tooltip, StringComparison.Ordinal);
        foreach (int count in new[] { report.WinTrades, report.LossTrades, report.BreakevenTrades })
        {
            Assert.Contains(count.ToString(System.Globalization.CultureInfo.CurrentCulture), tooltip, StringComparison.Ordinal);
        }
    }

    // ---- G7 -------------------------------------------------------------------------------------

    [Fact]
    public void Catalog_UnknownMetricName_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestMetricCatalog.Get("NotAMetric"));
        Assert.All(FixedMetricNames, name => Assert.Equal(name, BacktestMetricCatalog.Get(name).Name));
    }

    [Fact]
    public void Formatting_UndefinedKinds_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestMetricGroupBuilder.FormatValue((BacktestMetricValueFormat)999, 1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => BacktestMetricGroupBuilder.ResolveSemantic((BacktestMetricSemanticRule)999, 1m));
    }

    [Fact]
    public void Catalog_DefinitionsCoverEveryExtendedMetricAndAreUnique()
    {
        string[] names = BacktestMetricCatalog.Definitions.Select(d => d.Name).ToArray();
        Assert.Equal(ExpectedTotalRowCount, names.Length);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());

        BacktestReport report = GenerateReport(10m, -4m);
        Assert.All(report.EnumerateExtendedMetrics(), metric => Assert.Contains(metric.Name, names));
    }

    // ---- G8: closed world of the definition table -------------------------------------------------

    private static readonly BacktestMetricGroup[] AllGroups = Enum.GetValues<BacktestMetricGroup>();

    private static BacktestMetricDefinition Definition(
        string name,
        BacktestMetricGroup group = BacktestMetricGroup.Performance,
        int? summaryOrder = null,
        BacktestMetricValueFormat format = BacktestMetricValueFormat.FourDecimals,
        BacktestMetricSemanticRule rule = BacktestMetricSemanticRule.Neutral,
        bool alwaysPresent = true) =>
        new()
        {
            Name = name,
            Group = group,
            Format = format,
            Rule = rule,
            Select = _ => null,
            SummaryOrder = summaryOrder,
            IsAlwaysPresent = alwaysPresent,
        };

    [Fact]
    public void Catalog_GroupOrderListsEveryGroupExactlyOnce()
    {
        // A group missing from the order would silently hide all of its rows.
        Assert.Equal(AllGroups.Length, BacktestMetricCatalog.Groups.Length);
        Assert.Equal(AllGroups.OrderBy(group => group), BacktestMetricCatalog.Groups.OrderBy(group => group));
        Assert.All(BacktestMetricCatalog.Definitions, definition => Assert.Contains(definition.Group, BacktestMetricCatalog.Groups));
        Assert.All(AllGroups, group => Assert.Contains(BacktestMetricCatalog.Definitions, definition => definition.Group == group));
    }

    [Fact]
    public void Catalog_SummaryMetricsAreAlwaysPresent_SoALegacyReportStillGetsTheWholeSummary()
    {
        // The unbroken-sequence contract itself is enforced by Validate when the catalog is built (see the Validate_* tests);
        // the legacy-report summary is exercised by BacktestResultsSummaryTests.
        Assert.NotEmpty(BacktestMetricCatalog.SummaryDefinitions);
        Assert.All(BacktestMetricCatalog.SummaryDefinitions, definition => Assert.True(definition.IsAlwaysPresent, definition.Name));
    }

    [Fact]
    public void Validate_ConsistentTable_Passes()
    {
        BacktestMetricCatalog.Validate(new[] { Definition("A", summaryOrder: 1), Definition("B", BacktestMetricGroup.Drawdown, 2), Definition("C") }, AllGroups);
    }

    [Fact]
    public void Validate_GroupMissingFromTheOrder_Throws()
    {
        BacktestMetricGroup[] withoutLast = AllGroups.Take(AllGroups.Length - 1).ToArray();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => BacktestMetricCatalog.Validate(new[] { Definition("A") }, withoutLast));
        Assert.Contains(AllGroups[^1].ToString(), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_GroupListedTwice_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => BacktestMetricCatalog.Validate(new[] { Definition("A") }, AllGroups.Append(AllGroups[0]).ToArray()));
    }

    [Fact]
    public void Validate_DefinitionOfAnUnlistedGroup_Throws()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => BacktestMetricCatalog.Validate(new[] { Definition("Orphan", (BacktestMetricGroup)999) }, AllGroups));
        Assert.Contains("Orphan", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_DuplicateMetricName_Throws()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => BacktestMetricCatalog.Validate(new[] { Definition("Same"), Definition("Same", BacktestMetricGroup.Drawdown) }, AllGroups));
        Assert.Contains("Same", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(0, 1)]
    public void Validate_SummaryPositionsThatAreNotAnUnbrokenSequenceFromOne_Throw(int first, int second)
    {
        Assert.Throws<InvalidOperationException>(
            () => BacktestMetricCatalog.Validate(new[] { Definition("A", summaryOrder: first), Definition("B", summaryOrder: second) }, AllGroups));
    }

    [Fact]
    public void Validate_SummaryMetricThatAReportMayNotCarry_Throws()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => BacktestMetricCatalog.Validate(new[] { Definition("Optional", summaryOrder: 1, alwaysPresent: false) }, AllGroups));
        Assert.Contains("Optional", error.Message, StringComparison.Ordinal);

        // An optional metric outside the summary is fine.
        BacktestMetricCatalog.Validate(new[] { Definition("Optional", alwaysPresent: false) }, AllGroups);
    }

    [Fact]
    public void Validate_FormattedDefinitionTextNeedsArgumentsFallbackAndTooltipTogether()
    {
        BacktestMetricDefinition complete = Definition("A") with
        {
            HasDefinitionTooltip = true,
            DefinitionArguments = _ => new object[] { 1 },
            DefinitionFallbackFormat = "{0}",
        };
        BacktestMetricCatalog.Validate(new[] { complete }, AllGroups);

        Assert.Throws<InvalidOperationException>(() => BacktestMetricCatalog.Validate(new[] { complete with { DefinitionFallbackFormat = null } }, AllGroups));
        Assert.Throws<InvalidOperationException>(() => BacktestMetricCatalog.Validate(new[] { complete with { DefinitionArguments = null } }, AllGroups));
        Assert.Throws<InvalidOperationException>(() => BacktestMetricCatalog.Validate(new[] { complete with { HasDefinitionTooltip = false } }, AllGroups));
    }

    [Fact]
    public void Validate_RowsWithoutASummaryPosition_AreNotCountedAsDuplicates()
    {
        BacktestMetricCatalog.Validate(new[] { Definition("A"), Definition("B"), Definition("C", summaryOrder: 1) }, AllGroups);
    }

    [Fact]
    public void Validate_UndefinedFormatOrRule_ThrowsAndNamesTheOffendingMember()
    {
        ArgumentOutOfRangeException format = Assert.Throws<ArgumentOutOfRangeException>(
            () => BacktestMetricCatalog.Validate(new[] { Definition("A", format: (BacktestMetricValueFormat)999) }, AllGroups));
        ArgumentOutOfRangeException rule = Assert.Throws<ArgumentOutOfRangeException>(
            () => BacktestMetricCatalog.Validate(new[] { Definition("A", rule: (BacktestMetricSemanticRule)999) }, AllGroups));

        Assert.Equal(nameof(BacktestMetricDefinition.Format), format.ParamName);
        Assert.Equal(nameof(BacktestMetricDefinition.Rule), rule.ParamName);
    }
}
