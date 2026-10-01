using System.Collections.Immutable;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>SA_UI_INTERACTION section 12: the metric and summary tooltips follow Settings &gt; Fonts &gt; TooltipFontSize, set on the Tip content itself.</summary>
// Builds the real view, whose texts read the shared static LocalizationManager.Instance (see LocalizationSharedStateCollection.cs).
[Collection("LocalizationSharedState")]
public class BacktestResultsViewTooltipTests
{
    public BacktestResultsViewTooltipTests()
    {
        LocalizationManager.Instance.Initialize("en");
    }

    /// <summary>Sizes no default theme uses, and different from each other, so the assertion proves which setting was followed.</summary>
    private const double SentinelTooltipFontSize = 37.0;

    private const double SentinelHelperFontSize = 29.0;

    private const string TooltipFontSizeKey = "TooltipFontSize";
    private const string HelperFontSizeKey = "HelperFontSize";

    [AvaloniaFact]
    public void MetricTooltips_FollowTooltipFontSizeOnTheirContent_AndOnlyRowsWithTextHaveOne()
    {
        IResourceDictionary resources = Application.Current!.Resources;
        bool hadTooltip = resources.TryGetValue(TooltipFontSizeKey, out object? previousTooltip);
        bool hadHelper = resources.TryGetValue(HelperFontSizeKey, out object? previousHelper);
        resources[TooltipFontSizeKey] = SentinelTooltipFontSize;
        resources[HelperFontSizeKey] = SentinelHelperFontSize;
        var viewModel = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(),
            Mock.Of<IDialogService>());
        var view = new BacktestResultsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1100, Height = 900 };

        try
        {
            window.Show();
            (var result, BacktestReport report) = BacktestResultsViewInteractionTests.MakeRun(110m);
            viewModel.Update(result, report, 0);
            Render();

            // The hover targets of the metric tooltips: a visible host exists exactly for each row (table and summary) that has tooltip text.
            Border[] hosts = view.GetVisualDescendants().OfType<Border>()
                .Where(border => border.IsVisible && ToolTip.GetTip(border) is TextBlock)
                .ToArray();
            ImmutableArray<BacktestMetricDisplayRow> tableRows = viewModel.MetricGroups.SelectMany(group => group.Rows).ToImmutableArray();
            int expectedHosts = tableRows.Count(row => row.TooltipText is not null) + viewModel.Summary.Count(row => row.TooltipText is not null);

            Assert.NotEmpty(hosts);
            Assert.Equal(expectedHosts, hosts.Length);
            Assert.Contains(tableRows, row => row.TooltipText is null);
            Assert.All(hosts, host => Assert.False(string.IsNullOrEmpty(Assert.IsType<BacktestMetricDisplayRow>(host.DataContext).TooltipText)));

            // The opened popup shows the row's text at the Tooltip Font Size, not at the Helper Font Size.
            Border host = hosts[0];
            string expectedText = ((BacktestMetricDisplayRow)host.DataContext!).TooltipText!;
            ToolTip.SetIsOpen(host, true);
            Render();
            ToolTip popup = window.GetVisualDescendants().OfType<ToolTip>().Single();
            TextBlock shown = popup.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == expectedText);
            double shownSize = shown.FontSize;
            ToolTip.SetIsOpen(host, false);
            Render();

            Assert.Equal(SentinelTooltipFontSize, shownSize);
            Assert.NotEqual(SentinelHelperFontSize, shownSize);
        }
        finally
        {
            window.Close();
            Restore(resources, TooltipFontSizeKey, hadTooltip, previousTooltip);
            Restore(resources, HelperFontSizeKey, hadHelper, previousHelper);
        }
    }

    private static void Restore(IResourceDictionary resources, string key, bool hadValue, object? previous)
    {
        if (hadValue) resources[key] = previous; else resources.Remove(key);
    }

    private static void Render()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
