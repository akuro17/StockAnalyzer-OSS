using System.Collections.Generic;
using System.Linq;
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
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>
/// The Results tab's "Registered Conditions" section lists the live registered conditions, so a condition is
/// visible before the first Run and can be removed with the per-row delete button.
/// </summary>
public class BacktestRegisteredConditionsViewTests
{
    private sealed class EmptyCatalogProvider : IScreenerCatalogProvider
    {
        public IReadOnlyList<ScreenerCatalogItem> GetCatalogItems(IIndicatorFactory? indicatorFactory = null) => new List<ScreenerCatalogItem>();
        public CoreIndicatorSettings? GetDefaultSettings(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => null;
        public IReadOnlyList<string> GetOutputSeriesNames(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => new[] { "Main" };
    }

    private static BacktestConditionEntry Entry(BacktestConditionRole role, PriceType left, PriceType right) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = left },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.Indicator,
        Right = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = right },
        Role = role,
        Position = TradeSide.Long,
    };

    private static void Render()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void RegisteredConditions_AreListedBeforeRun_AndDeleteButtonRemovesTheRow()
    {
        var selection = new BacktestIndicatorSelectionViewModel(new EmptyCatalogProvider(), NullLocalizationService.Instance);
        var results = new BacktestResultsViewModel(
            NullLocalizationService.Instance,
            new Mock<IBacktestReportExporter>().Object,
            new Mock<IDialogService>().Object)
        {
            ConditionSelection = selection,
        };
        var view = new BacktestResultsView { DataContext = results };
        var window = new Window { Content = view, Width = 1100, Height = 900 };

        try
        {
            window.Show();
            Render();
            Assert.False(results.HasResult);
            Assert.Empty(view.GetVisualDescendants().OfType<Button>().Where(IsDeleteButton));

            selection.ConditionEntries.Add(Entry(BacktestConditionRole.EntryOnly, PriceType.Open, PriceType.Close));
            selection.ConditionEntries.Add(Entry(BacktestConditionRole.ExitOnly, PriceType.Low, PriceType.Close));
            Render();

            List<Button> deleteButtons = view.GetVisualDescendants().OfType<Button>().Where(IsDeleteButton).ToList();
            Assert.Equal(2, deleteButtons.Count);

            deleteButtons[0].Command!.Execute(deleteButtons[0].CommandParameter);
            Render();

            Assert.Single(selection.ConditionEntries);
            Assert.Equal(BacktestConditionRole.ExitOnly, selection.ConditionEntries[0].Role);
            Assert.Single(view.GetVisualDescendants().OfType<Button>().Where(IsDeleteButton));
        }
        finally
        {
            window.Close();
        }
    }

    private static bool IsDeleteButton(Button button) =>
        global::Avalonia.Automation.AutomationProperties.GetAutomationId(button) == "Backtest_RegisteredCondition_Delete";
}
