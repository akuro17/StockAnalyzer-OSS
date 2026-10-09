using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Tests.Views.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// Tests BacktestMetricsColumnChooserWindow and BacktestMetricsColumnChooserViewModel
/// for compliance with SA_UI_SUBWINDOW_STANDARD.md, category filtering, boundary invariants,
/// and ResultsViewModel summary immunity and category pruning.
/// </summary>
[Collection("LocalizationSharedState")]
public class BacktestMetricsColumnChooserWindowTests
{
    private static BacktestMetricsColumnChooserWindow ShowWindow(BacktestMetricsColumnChooserViewModel vm)
    {
        var window = new BacktestMetricsColumnChooserWindow { DataContext = vm };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Window_AdheresToSubWindowDecorationsAndResizeStandard()
    {
        var vm = new BacktestMetricsColumnChooserViewModel();
        var window = ShowWindow(vm);
        try
        {
            Assert.True(window.ExtendClientAreaToDecorationsHint);
            Assert.False(window.CanResize);
            Assert.Equal(740, window.Width);
            Assert.Equal(580, window.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ScrollBars_ComplyWithSubWindowStandard()
    {
        var vm = new BacktestMetricsColumnChooserViewModel();
        var window = ShowWindow(vm);
        try
        {
            var listBoxes = window.GetVisualDescendants().OfType<ListBox>().ToList();
            Assert.True(listBoxes.Count >= 2, "Expected at least Categories and Metrics ListBoxes");

            foreach (var lb in listBoxes)
            {
                Assert.False(ScrollViewer.GetAllowAutoHide(lb), $"ListBox must set AllowAutoHide='False'");
                Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(lb));
                Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(lb));
            }

            var scrollViewer = window.GetVisualDescendants().OfType<ScrollViewer>()
                .FirstOrDefault(sv => sv.Parent is Border border && Grid.GetColumn(border) == 2);
            Assert.NotNull(scrollViewer);
            Assert.False(ScrollViewer.GetAllowAutoHide(scrollViewer));
            Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(scrollViewer));
            Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(scrollViewer));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Footer_AdheresToButtonStandard()
    {
        var vm = new BacktestMetricsColumnChooserViewModel();
        var window = ShowWindow(vm);
        try
        {
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            var okBtn = buttons.FirstOrDefault(b => b.IsDefault);
            Assert.NotNull(okBtn);

            var cancelBtn = buttons.FirstOrDefault(b => b.IsCancel);
            Assert.NotNull(cancelBtn);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void ViewModel_InitializesWithAll34MetricsByDefault()
    {
        var vm = new BacktestMetricsColumnChooserViewModel();
        Assert.Equal(34, vm.AllItems.Count);
        Assert.All(vm.AllItems, item => Assert.True(item.IsActive));
        Assert.Equal(34, vm.GetActiveMetricNames().Count);
    }

    [Fact]
    public void ViewModel_CategoryFiltering_All8Categories()
    {
        var vm = new BacktestMetricsColumnChooserViewModel(new[] { "TotalReturn", "CAGR", "MaxDrawdown" });
        Assert.Equal(34, vm.AllItems.Count);

        // 1. Active: only 3 active items
        Assert.Equal(BacktestMetricsCategory.Active, vm.SelectedCategory);
        Assert.Equal(3, vm.FilteredItems.Count);
        Assert.Contains(vm.FilteredItems, x => x.MetricName == "TotalReturn");
        Assert.Contains(vm.FilteredItems, x => x.MetricName == "CAGR");
        Assert.Contains(vm.FilteredItems, x => x.MetricName == "MaxDrawdown");

        // 2. All: all 34 items
        vm.SelectedCategory = BacktestMetricsCategory.All;
        Assert.Equal(34, vm.FilteredItems.Count);

        // 3-8. Category groups
        var groups = new[]
        {
            (BacktestMetricsCategory.Performance, BacktestMetricGroup.Performance),
            (BacktestMetricsCategory.Drawdown, BacktestMetricGroup.Drawdown),
            (BacktestMetricsCategory.RiskAdjusted, BacktestMetricGroup.RiskAdjusted),
            (BacktestMetricsCategory.TradeStatistics, BacktestMetricGroup.TradeStatistics),
            (BacktestMetricsCategory.TradeBehavior, BacktestMetricGroup.TradeBehavior),
            (BacktestMetricsCategory.RiskEfficiency, BacktestMetricGroup.RiskEfficiency),
        };

        foreach (var (cat, grp) in groups)
        {
            vm.SelectedCategory = cat;
            int expectedCount = BacktestMetricCatalog.Definitions.Count(d => d.Group == grp);
            Assert.Equal(expectedCount, vm.FilteredItems.Count);
            Assert.All(vm.FilteredItems, item => Assert.Equal(grp, item.Group));
        }
    }

    [Fact]
    public void ViewModel_IdentityCategoryIsStrictlyAbsent()
    {
        string[] categoryNames = Enum.GetNames(typeof(BacktestMetricsCategory));
        Assert.DoesNotContain("Identity", categoryNames, StringComparer.OrdinalIgnoreCase);

        string xamlPath = Path.Combine(StockAnalyzer.Avalonia.Tests.TestHelpers.TestSolution.Root, "StockAnalyzer.Avalonia", "Views", "Dialogs", "BacktestMetricsColumnChooserWindow.axaml");
        string xaml = File.ReadAllText(xamlPath);
        Assert.DoesNotContain("Identity", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ViewModel_SearchQuery_FiltersCorrectly()
    {
        var vm = new BacktestMetricsColumnChooserViewModel();
        vm.SelectedCategory = BacktestMetricsCategory.All;

        vm.SearchQuery = "Drawdown";
        Assert.True(vm.FilteredItems.Count >= 1);
        Assert.All(vm.FilteredItems, x => Assert.True(
            x.MetricName.Contains("Drawdown", StringComparison.OrdinalIgnoreCase) ||
            x.DisplayName.Contains("Drawdown", StringComparison.OrdinalIgnoreCase) ||
            x.Description.Contains("Drawdown", StringComparison.OrdinalIgnoreCase)));

        vm.SearchQuery = string.Empty;
        Assert.Equal(34, vm.FilteredItems.Count);
    }

    [Fact]
    public void ViewModel_EnforcesAtLeastOneActiveMetricInvariant()
    {
        var vm = new BacktestMetricsColumnChooserViewModel(new[] { "TotalReturn" });
        Assert.Single(vm.AllItems.Where(x => x.IsActive));

        var activeItem = vm.AllItems.First(x => x.IsActive);
        Assert.Equal("TotalReturn", activeItem.MetricName);

        // Attempt to uncheck the sole remaining active metric
        activeItem.IsActive = false;

        // Invariant check: must reject and stay true
        Assert.True(activeItem.IsActive);
        Assert.Single(vm.GetActiveMetricNames());
    }


    [Fact]
    public void XamlSource_UsesListScrollGutter_AndNoHardcodedFontSizes()
    {
        string xamlPath = Path.Combine(StockAnalyzer.Avalonia.Tests.TestHelpers.TestSolution.Root, "StockAnalyzer.Avalonia", "Views", "Dialogs", "BacktestMetricsColumnChooserWindow.axaml");
        Assert.True(File.Exists(xamlPath), $"XAML file must exist at {xamlPath}");

        string xaml = File.ReadAllText(xamlPath);

        // Standard requirement: scrollable lists must use shared ListScrollGutter
        Assert.Contains("Padding=\"{DynamicResource ListScrollGutter}\"", xaml);

        // Standard requirement: hardcoded font sizes in XAML view files are prohibited
        var matches = System.Text.RegularExpressions.Regex.Matches(xaml, @"FontSize=""(\d+)""");
        Assert.Empty(matches);

        // Standard requirement: Theme buttons
        Assert.Contains("Theme=\"{DynamicResource PrimaryButtonStyle}\"", xaml);
        Assert.Contains("Theme=\"{DynamicResource SecondaryButtonStyle}\"", xaml);
    }

    [Fact]
    public void ResultsViewModel_SetActiveMetricNames_UpdatesPlaceholderAndEnforcesInvariant()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        // Initial default: all 34 active
        Assert.Equal(34, vm.ActiveMetricNames.Count);
        Assert.True(vm.HasMetricGroups);
        Assert.Equal(34, vm.MetricGroups.Sum(g => g.Rows.Length));

        // Set to 2 metrics
        vm.SetActiveMetricNames(new[] { "TotalReturn", "CAGR" });
        Assert.Equal(2, vm.ActiveMetricNames.Count);
        Assert.True(vm.HasMetricGroups);
        // Category pruning: only 1 group ("Performance") contains rows
        Assert.Single(vm.MetricGroups);
        Assert.Equal(2, vm.MetricGroups[0].Rows.Length);

        // Empty set attempt is ignored by boundary invariant
        vm.SetActiveMetricNames(Array.Empty<string>());
        Assert.Equal(2, vm.ActiveMetricNames.Count);
    }

    [Fact]
    public void ResultsViewModel_SummaryCardImmunity_WhenSummaryMetricsHidden()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        (BacktestResult result, BacktestReport report) = BacktestResultsViewInteractionTests.MakeRun(110m);
        vm.Update(result, report, 0);

        Assert.True(vm.HasSummary);
        Assert.Equal(5, vm.Summary.Length);

        // Explicitly hide all 5 summary metrics from active metrics:
        // (Summary metrics are TotalReturn, CAGR, MaxDrawdown, AnnualizedSharpe, ClosedTrades)
        // Keep only ProfitFactor which is not in the Summary strip.
        vm.SetActiveMetricNames(new[] { "ProfitFactor" });

        // Table metric groups now only contains ProfitFactor
        Assert.Single(vm.MetricGroups);
        Assert.Single(vm.MetricGroups[0].Rows);
        Assert.Equal("Backtest_Metric_ProfitFactor", vm.MetricGroups[0].Rows[0].LabelKey);

        // Summary strip remains completely intact with 5 items (immune to table row filtering)
        Assert.True(vm.HasSummary);
        Assert.Equal(5, vm.Summary.Length);
    }

    [Fact]
    public void ResultsViewModel_CategoryPruning_EmptyGroupsOmitted()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        (BacktestResult result, BacktestReport report) = BacktestResultsViewInteractionTests.MakeRun(110m);
        vm.Update(result, report, 0);

        // Initially 6 groups
        Assert.Equal(6, vm.MetricGroups.Length);

        // Filter to single metric from Drawdown group
        vm.SetActiveMetricNames(new[] { "MaxDrawdown" });

        // Exactly 1 group remains, other 5 empty groups pruned
        Assert.Single(vm.MetricGroups);
        Assert.Equal("Backtest_MetricGroup_Drawdown", vm.MetricGroups[0].GroupKey);
        Assert.Single(vm.MetricGroups[0].Rows);
    }

    [Fact]
    public void ViewModel_InitializesWithActiveOrder_PreservingSequence()
    {
        var customOrder = new[] { "CAGR", "TotalReturn", "MaxDrawdown" };
        var vm = new BacktestMetricsColumnChooserViewModel(customOrder);

        var activeNames = vm.GetActiveMetricNames();
        Assert.Equal(customOrder, activeNames);

        // FilteredItems under Active category also reflects customOrder
        Assert.Equal(BacktestMetricsCategory.Active, vm.SelectedCategory);
        Assert.Equal(3, vm.FilteredItems.Count);
        Assert.Equal("CAGR", vm.FilteredItems[0].MetricName);
        Assert.Equal("TotalReturn", vm.FilteredItems[1].MetricName);
        Assert.Equal("MaxDrawdown", vm.FilteredItems[2].MetricName);
    }

    [Fact]
    public void ViewModel_ReorderActiveItems_UpdatesGetActiveMetricNamesAndFilteredItems()
    {
        var vm = new BacktestMetricsColumnChooserViewModel(new[] { "TotalReturn", "CAGR", "MaxDrawdown" });

        // Move first item to index 1 (swap TotalReturn and CAGR)
        vm.AllItems.Move(0, 1);

        var activeNames = vm.GetActiveMetricNames();
        Assert.Equal(new[] { "CAGR", "TotalReturn", "MaxDrawdown" }, activeNames);

        // FilteredItems under Active category automatically updates via CollectionChanged
        Assert.Equal(BacktestMetricsCategory.Active, vm.SelectedCategory);
        Assert.Equal(3, vm.FilteredItems.Count);
        Assert.Equal("CAGR", vm.FilteredItems[0].MetricName);
        Assert.Equal("TotalReturn", vm.FilteredItems[1].MetricName);
        Assert.Equal("MaxDrawdown", vm.FilteredItems[2].MetricName);
    }

    [Fact]
    public void ResultsViewModel_SetActiveMetricNames_PreservesSequenceOrder()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        var customOrder = new[] { "CAGR", "TotalReturn", "ProfitFactor" };
        vm.SetActiveMetricNames(customOrder);

        Assert.Equal(customOrder, vm.ActiveMetricNames);
    }

    [Fact]
    public void ResultsViewModel_SetActiveMetricNames_SavesToInjectedSettingsManager()
    {
        var settingsMock = new Mock<StockAnalyzer.Core.Services.Backtest.Configuration.IBacktestColumnSettingsManager>();
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>(),
            settingsMock.Object);

        var customOrder = new[] { "CAGR", "TotalReturn" };
        vm.SetActiveMetricNames(customOrder);

        settingsMock.Verify(m => m.SaveColumnsAsync(
            It.IsAny<IReadOnlyList<string>>(),
            It.Is<IReadOnlyList<string>>(metrics => metrics.SequenceEqual(customOrder))), Times.Once);
    }

    [Fact]
    public void ResultsViewModel_SetActiveMetricNames_ReordersPlaceholderMetricGroupsAndRows()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        // 1. Reorder within same group (Performance: CAGR before TotalReturn)
        vm.SetActiveMetricNames(new[] { "CAGR", "TotalReturn" });
        Assert.Single(vm.MetricGroups);
        Assert.Equal("Backtest_MetricGroup_Performance", vm.MetricGroups[0].GroupKey);
        Assert.Equal(2, vm.MetricGroups[0].Rows.Length);
        Assert.Equal("Backtest_Metric_CAGR", vm.MetricGroups[0].Rows[0].LabelKey);
        Assert.Equal("Backtest_Metric_TotalReturn", vm.MetricGroups[0].Rows[1].LabelKey);

        // 2. Reorder across groups (Drawdown before Performance)
        vm.SetActiveMetricNames(new[] { "MaxDrawdown", "TotalReturn" });
        Assert.Equal(2, vm.MetricGroups.Length);
        Assert.Equal("Backtest_MetricGroup_Drawdown", vm.MetricGroups[0].GroupKey);
        Assert.Equal("Backtest_Metric_MaxDrawdown", vm.MetricGroups[0].Rows[0].LabelKey);
        Assert.Equal("Backtest_MetricGroup_Performance", vm.MetricGroups[1].GroupKey);
        Assert.Equal("Backtest_Metric_TotalReturn", vm.MetricGroups[1].Rows[0].LabelKey);
    }

    [Fact]
    public void ResultsViewModel_SetActiveMetricNames_ReordersMetricGroupsAndRowsAfterRun()
    {
        var vm = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());

        (BacktestResult result, BacktestReport report) = BacktestResultsViewInteractionTests.MakeRun(110m);
        vm.Update(result, report, 0);

        // Reorder across groups after backtest run
        vm.SetActiveMetricNames(new[] { "MaxDrawdown", "CAGR", "TotalReturn" });
        Assert.Equal(2, vm.MetricGroups.Length);
        Assert.Equal("Backtest_MetricGroup_Drawdown", vm.MetricGroups[0].GroupKey);
        Assert.Single(vm.MetricGroups[0].Rows);
        Assert.Equal("Backtest_Metric_MaxDrawdown", vm.MetricGroups[0].Rows[0].LabelKey);

        Assert.Equal("Backtest_MetricGroup_Performance", vm.MetricGroups[1].GroupKey);
        Assert.Equal(2, vm.MetricGroups[1].Rows.Length);
        Assert.Equal("Backtest_Metric_CAGR", vm.MetricGroups[1].Rows[0].LabelKey);
        Assert.Equal("Backtest_Metric_TotalReturn", vm.MetricGroups[1].Rows[1].LabelKey);
    }

    [Fact]
    public void ViewModel_DuplicateActiveMetrics_FilteredDefensively()
    {
        var vm = new BacktestMetricsColumnChooserViewModel(new[] { "TotalReturn", "TotalReturn", "CAGR" });
        Assert.Equal(34, vm.AllItems.Count);
        Assert.Equal(2, vm.GetActiveMetricNames().Count);
        Assert.Equal(new[] { "TotalReturn", "CAGR" }, vm.GetActiveMetricNames());
    }

    [Fact]
    public void ViewModel_Dispose_UnsubscribesHandlersGracefully()
    {
        var vm = new BacktestMetricsColumnChooserViewModel();
        vm.Dispose(); // Must execute without throwing
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("en")]
    public void ViewModel_MetricDescriptions_DoNotContainBracketedPlaceholders(string lang)
    {
        string originalLang = LocalizationManager.Instance.CurrentLanguage;
        try
        {
            LocalizationManager.Instance.Initialize(lang);
            var loc = new LocalizationService();
            var vm = new BacktestMetricsColumnChooserViewModel(localization: loc);

            Assert.Equal(34, vm.AllItems.Count);

            foreach (var item in vm.AllItems)
            {
                // Description should be populated
                Assert.False(string.IsNullOrWhiteSpace(item.Description), $"Metric '{item.MetricName}' should have a non-empty Description.");

                // Description must NOT look like a raw variable name/bracketed resource key
                Assert.False(item.Description.StartsWith("[") && item.Description.EndsWith("]"),
                    $"Metric '{item.MetricName}' has bracketed variable description: '{item.Description}'");

                // DisplayName and GroupDisplayName must also NOT be bracketed
                Assert.False(item.DisplayName.StartsWith("[") && item.DisplayName.EndsWith("]"),
                    $"Metric '{item.MetricName}' has bracketed DisplayName: '{item.DisplayName}'");
                Assert.False(item.GroupDisplayName.StartsWith("[") && item.GroupDisplayName.EndsWith("]"),
                    $"Metric '{item.MetricName}' has bracketed GroupDisplayName: '{item.GroupDisplayName}'");
            }

            // ClosedTrades should use its dedicated description
            var closedTrades = vm.AllItems.First(x => x.MetricName == "ClosedTrades");
            Assert.Contains(lang == "ja" ? "決済が完了したトレード" : "Total count of closed", closedTrades.Description);
        }
        finally
        {
            LocalizationManager.Instance.Initialize(originalLang);
        }
    }

    [Fact]
    public void LocalizationService_TryGetString_ReturnsFalseForMissingKeys()
    {
        var loc = new LocalizationService();
        Assert.False(loc.TryGetString("DefinitelyNonExistentKey_ABC_123", out var missingVal));
        Assert.Null(missingVal);

        Assert.False(loc.TryGetString(string.Empty, out var emptyVal));
        Assert.Null(emptyVal);

        Assert.True(loc.TryGetString("Btn_OK", out var okVal));
        Assert.False(string.IsNullOrEmpty(okVal));
        Assert.False(okVal!.StartsWith("["));
    }
}
