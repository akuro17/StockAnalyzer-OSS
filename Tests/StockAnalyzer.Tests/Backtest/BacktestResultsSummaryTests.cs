using System;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;
using static StockAnalyzer.Tests.Backtest.BacktestReportTestFactory;

namespace StockAnalyzer.Tests.Backtest;

/// <summary>Result summary at the top of the Results tab (Y:\Temp\sa_implementation_plan_BacktestResultsSummaryStrip.md): real generated reports, no mocks.</summary>
public class BacktestResultsSummaryTests
{
    private static readonly string[] ExpectedSummaryLabelKeys =
    {
        "Backtest_Metric_TotalReturn",
        "Backtest_Metric_CAGR",
        "Backtest_Metric_MaxDrawdown",
        "Backtest_Metric_AnnualizedSharpe",
        "Backtest_Metric_ClosedTrades",
    };

    private static ImmutableArray<BacktestMetricGroupPresentation> Groups(BacktestReport report, ILocalizationService localization) =>
        BacktestMetricGroupBuilder.Build(report, localization);

    // ---- S1 -------------------------------------------------------------------------------------

    [Fact]
    public void BuildSummary_ReturnsTheFiveMetricsInSummaryOrder()
    {
        ImmutableArray<BacktestMetricGroupPresentation> groups = Groups(GenerateReport(10m, -4m, 6m, -2m), NullLocalizationService.Instance);

        ImmutableArray<BacktestMetricDisplayRow> summary = BacktestMetricGroupBuilder.BuildSummary(groups);

        Assert.Equal(ExpectedSummaryLabelKeys, summary.Select(row => row.LabelKey));
    }

    // ---- S2 -------------------------------------------------------------------------------------

    [Fact]
    public void BuildSummary_ItemsAreTheSameRowObjectsAsTheTable()
    {
        ImmutableArray<BacktestMetricGroupPresentation> groups = Groups(GenerateReport(10m, -4m, 6m, -2m), LoadLocale("en"));

        ImmutableArray<BacktestMetricDisplayRow> summary = BacktestMetricGroupBuilder.BuildSummary(groups);

        ImmutableArray<BacktestMetricDisplayRow> tableRows = groups.SelectMany(g => g.Rows).ToImmutableArray();
        foreach (BacktestMetricDisplayRow item in summary)
        {
            Assert.Same(tableRows.Single(row => row.LabelKey == item.LabelKey), item);
        }
    }

    // ---- S3 -------------------------------------------------------------------------------------

    [Fact]
    public void BuildSummary_NoTrades_ShowsNotAvailableForSharpeAndZeroTrades()
    {
        FakeLocalizationService locale = LoadLocale("en");
        ImmutableArray<BacktestMetricGroupPresentation> groups = Groups(GenerateReport(), locale);

        ImmutableArray<BacktestMetricDisplayRow> summary = BacktestMetricGroupBuilder.BuildSummary(groups);

        BacktestMetricDisplayRow sharpe = summary.Single(row => row.LabelKey == "Backtest_Metric_AnnualizedSharpe");
        Assert.NotEqual(MetricStatus.Valid, sharpe.Status);
        Assert.Equal(locale.GetString("Backtest_Metric_NotAvailable"), sharpe.ValueText);
        Assert.Equal(BacktestMetricSemantic.Neutral, sharpe.Semantic);
        Assert.False(string.IsNullOrEmpty(sharpe.TooltipText));

        BacktestMetricDisplayRow trades = summary.Single(row => row.LabelKey == "Backtest_Metric_ClosedTrades");
        Assert.Equal("0", trades.ValueText);
    }

    // ---- S4 -------------------------------------------------------------------------------------

    [Fact]
    public void BuildSummary_LegacyReport_HasAllFiveItems()
    {
        ImmutableArray<BacktestMetricGroupPresentation> groups = Groups(BacktestTestFactory.CreateStubReport(), NullLocalizationService.Instance);

        ImmutableArray<BacktestMetricDisplayRow> summary = BacktestMetricGroupBuilder.BuildSummary(groups);

        Assert.Equal(ExpectedSummaryLabelKeys, summary.Select(row => row.LabelKey));
    }

    // ---- S5 -------------------------------------------------------------------------------------

    [Fact]
    public void SelectRows_NameWithoutARow_ThrowsInvalidOperation()
    {
        ImmutableArray<BacktestMetricGroupPresentation> groups = Groups(GenerateReport(10m, -4m), NullLocalizationService.Instance);

        Assert.Throws<InvalidOperationException>(() => BacktestMetricGroupBuilder.SelectRows(groups, new[] { "NotAMetric" }));
    }

    // ---- S6 -------------------------------------------------------------------------------------

    private static BacktestResultsViewModel CreateViewModel() =>
        new(NullLocalizationService.Instance, new FakeBacktestReportExporter(), new FakeDialogService());

    [Fact]
    public void Update_PublishesTheSummaryAndReplacesItOnTheNextUpdate()
    {
        BacktestResultsViewModel vm = CreateViewModel();
        var raised = new System.Collections.Generic.List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(vm.HasSummary);
        Assert.Equal(0, vm.Summary.Length);

        vm.Update(BacktestTestFactory.CreateResult(), GenerateReport(10m, -4m, 6m, -2m), 0);

        Assert.True(vm.HasSummary);
        Assert.Equal(ExpectedSummaryLabelKeys, vm.Summary.Select(row => row.LabelKey));
        Assert.Contains(nameof(BacktestResultsViewModel.Summary), raised);
        Assert.Contains(nameof(BacktestResultsViewModel.HasSummary), raised);
        string firstTrades = vm.Summary.Single(row => row.LabelKey == "Backtest_Metric_ClosedTrades").ValueText;
        Assert.Equal("4", firstTrades);

        vm.Update(BacktestTestFactory.CreateResult(), GenerateReport(10m, 6m, 3m), 0);

        Assert.Equal("3", vm.Summary.Single(row => row.LabelKey == "Backtest_Metric_ClosedTrades").ValueText);
    }

    [Fact]
    public void Update_SummaryRowsAreTheTableRows()
    {
        BacktestResultsViewModel vm = CreateViewModel();

        vm.Update(BacktestTestFactory.CreateResult(), GenerateReport(10m, -4m, 6m, -2m), 0);

        var tableRows = vm.MetricGroups.SelectMany(group => group.Rows).ToList();
        foreach (BacktestMetricDisplayRow item in vm.Summary)
        {
            Assert.Contains(tableRows, row => ReferenceEquals(row, item));
        }
    }

    [Fact]
    public void Catalog_SummaryOrderIsUniqueAndContiguous()
    {
        int?[] orders = BacktestMetricCatalog.Definitions.Select(d => d.SummaryOrder).Where(o => o is not null).OrderBy(o => o).ToArray();

        Assert.Equal(new int?[] { 1, 2, 3, 4, 5 }, orders);
        Assert.Equal(5, BacktestMetricCatalog.SummaryDefinitions.Length);
    }
}
