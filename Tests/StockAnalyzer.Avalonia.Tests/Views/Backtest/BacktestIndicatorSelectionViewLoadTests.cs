using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Interfaces;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>
/// Regression guard for a real runtime bug reported by the user ("Failed to initialize Backtest
/// window: Unable to cast object of type 'Avalonia.Controls.GridLength' to type
/// 'Avalonia.Controls.ColumnDefinitions'"): Task 6's condition-builder XAML used
/// <c>ColumnDefinitions="{StaticResource ...}, *"</c> - a StaticResource markup extension mixed into
/// the shorthand ColumnDefinitions attribute string. This parses and builds fine (it is not caught at
/// `dotnet build` time), but throws the moment the view is actually instantiated, which happens when
/// BacktestWindow opens its Indicator Selection tab. Fixed by using a literal ColumnDefinitions string
/// instead of a resource reference. This test mounts the real view (not just the ViewModel, which would
/// not exercise the XAML parser at all) so a similar mistake in this file would fail here instead of
/// only at runtime for a real user.
///
/// Updated for the SAで改善 (2026-09-18) right-column redesign: the "Track Indicator" tab and its
/// TabControl were removed (Comparison Condition is now the column's only content), so this test now
/// switches to Indicator mode instead of a tab index to still exercise the conditionally-visible Right
/// Target (Indicator mode) card's own XAML.
/// </summary>
public class BacktestIndicatorSelectionViewLoadTests
{
    private sealed class FakeCatalogProvider : IScreenerCatalogProvider
    {
        public IReadOnlyList<ScreenerCatalogItem> GetCatalogItems(IIndicatorFactory? indicatorFactory = null) => new[]
        {
            new ScreenerCatalogItem { CategoryType = ScreenerItemCategoryType.Indicator, GroupName = "MA", ShortName = "SMA", DisplayName = "Simple Moving Average", IndicatorType = IndicatorType.SMA },
        };

        public CoreIndicatorSettings? GetDefaultSettings(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => null;

        public IReadOnlyList<string> GetOutputSeriesNames(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => new[] { "Main" };
    }

    [AvaloniaFact]
    public void OffsetInputs_AreBoundToTheRuleMinimumAndTheConfiguredMaximum()
    {
        const int ConfiguredMax = 123;
        var settings = new Moq.Mock<StockAnalyzer.Core.Services.IStockAnalyzerSettings>();
        settings.Setup(s => s.BacktestMaxConditionOffset).Returns(ConfiguredMax);
        var viewModel = new BacktestIndicatorSelectionViewModel(new FakeCatalogProvider(), NullLocalizationService.Instance, toastService: null, indicatorFactory: null, settings: settings.Object);
        var view = new BacktestIndicatorSelectionView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 700 };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var offsetBoxes = view.GetVisualDescendants().OfType<NumericUpDown>().Where(n => n.Maximum == ConfiguredMax).ToList();
        Assert.Equal(ConfiguredMax, viewModel.MaxConditionOffset);
        Assert.Equal(StockAnalyzer.Core.Services.Backtest.Engine.BacktestConditionOffsetRule.MinOffset, viewModel.MinConditionOffset);
        Assert.True(offsetBoxes.Count >= 2, "Both the Left and the Right Offset input must take their Maximum from the configured setting.");
        Assert.All(offsetBoxes, box => Assert.Equal(viewModel.MinConditionOffset, box.Minimum));
        window.Close();
    }

    [AvaloniaFact]
    public void View_LoadsAndRendersIndicatorModeCard_WithoutThrowing()
    {
        var viewModel = new BacktestIndicatorSelectionViewModel(new FakeCatalogProvider(), NullLocalizationService.Instance);
        var view = new BacktestIndicatorSelectionView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 700 };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        // The Right Target (Indicator mode) card is only IsVisible once ConditionTargetMode switches to
        // Indicator - realizing it here exercises that card's own XAML (Output/Timeframe/Offset rows)
        // in addition to what is already visible by default (Left Target card, Numeric-mode Right card).
        viewModel.SetConditionRightModeIndicatorCommand.Execute(null);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        window.Close();
    }
}
