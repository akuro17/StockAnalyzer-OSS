using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels.Backtest;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Core.Interfaces;
using TimeFrame = StockAnalyzer.Core.Models.TimeFrame;
using StockAnalyzer.Core.Models.Backtest.Engine;
using StockAnalyzer.Core.Services.Backtest.Reporting;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Backtest;

/// <summary>BF01 acceptance using the actual view, Fluent templates and immutable backtest results.</summary>
[Collection("LocalizationSharedState")]
public class BacktestResultsViewLayoutTests
{
    private const double ViewportHeight = 320;
    private const int OverflowTradeCount = 60;
    private const double LayoutTolerance = 0.01;
    private const string Prefix = "Backtest_Results_";

    public BacktestResultsViewLayoutTests() => LocalizationManager.Instance.Initialize("en");

    [AvaloniaTheory]
    [InlineData(800)]
    [InlineData(1200)]
    [InlineData(1600)]
    public void Details_AreDirectlyBelowEquity_WithTwoBoundedNonCollapsibleColumns(double width)
    {
        var (window, view, vm) = Mount(width);
        try
        {
            Border card = Find<Border>(view, "DetailsBlock");
            Grid grid = Assert.IsType<Grid>(card.Child);
            Border divider = Find<Border>(view, "DetailsSeparator");
            ScrollViewer metrics = Find<ScrollViewer>(view, "MetricsScroll");
            ScrollViewer trades = Find<ScrollViewer>(view, "TradesScroll");
            Assert.Empty(card.GetVisualDescendants().OfType<Expander>());
            Assert.True(card.IsVisible);
            Assert.False(vm.HasResult);
            var outerStack = Assert.IsType<StackPanel>(card.Parent);
            int index = outerStack.Children.IndexOf(card);
            Border equity = Assert.IsType<Border>(outerStack.Children[index - 1]);
            Assert.Single(equity.GetVisualDescendants().OfType<EquityCurveControl>());
            Expander audit = Assert.IsType<Expander>(outerStack.Children[index + 1]);
            Assert.False(audit.IsVisible);
            Assert.Equal(1, divider.Bounds.Width);
            Assert.InRange(Math.Abs(divider.Bounds.Height - grid.Bounds.Height), 0, LayoutTolerance);
            Assert.Equal(2, Grid.GetColumn(trades));
            Assert.InRange(Math.Abs(metrics.Bounds.Width / trades.Bounds.Width - 2d / 3d), 0, LayoutTolerance);
            foreach (ScrollViewer scroll in new[] { metrics, trades })
            {
                Assert.Equal(ViewportHeight, scroll.Bounds.Height);
                Assert.Equal(ViewportHeight, scroll.MaxHeight);
                Assert.False(scroll.AllowAutoHide);
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                Assert.Equal(ScrollBarVisibility.Auto, scroll.HorizontalScrollBarVisibility);
                Assert.True(scroll.Focusable);
            }
            Assert.Equal(0, metrics.TabIndex);
            Assert.Equal(1, trades.TabIndex);
            Assert.Equal(2, Find<Button>(view, "ExportButton").TabIndex);
            Assert.False(vm.ExportReportCommand.CanExecute(null));
            Assert.False(Find<Button>(view, "ExportButton").IsEffectivelyEnabled);
            Button toggleBtn = Find<Button>(view, "ToggleEquityDataPanel");
            toggleBtn.Command!.Execute(null);
            Render();
            Assert.True(card.IsVisible);
            Assert.Equal(ViewportHeight, metrics.Bounds.Height);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Overflow_RemainsNonOverlappingAndIndependent_WithAlignedHeadersAndAllTrades()
    {
        var (window, view, vm) = Mount(800);
        try
        {
            Publish(vm, OverflowTradeCount);
            vm.SelectedTradePageSize = vm.TradePageSizeOptions.First(o => o.Value == 100);
            Render();
            ScrollViewer metrics = Find<ScrollViewer>(view, "MetricsScroll");
            ScrollViewer trades = Find<ScrollViewer>(view, "TradesScroll");
            AssertOverflowGeometry(metrics);
            AssertOverflowGeometry(trades);
            ItemsControl tradeItems = Find<ItemsControl>(view, "Trades");
            Assert.Equal(vm.PagedTrades, tradeItems.ItemsSource!.Cast<BacktestTradeRow>());
            Assert.Equal(OverflowTradeCount, vm.Trades.Length);
            AssertColumnsAligned(metrics, Find<ItemsControl>(view, "Metrics"));
            AssertColumnsAligned(trades, tradeItems);

            metrics.Offset = metrics.ScrollBarMaximum;
            Render();
            Assert.Equal(default, trades.Offset);
            Assert.Equal(metrics.ScrollBarMaximum, metrics.Offset);
            Assert.Equal(6, vm.MetricGroups.Length);

            trades.Offset = trades.ScrollBarMaximum;
            Render();
            Assert.Equal(metrics.ScrollBarMaximum, metrics.Offset);
            TextBlock lastId = tradeItems.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.DataContext is BacktestTradeRow row &&
                    row.TradeId == OverflowTradeCount && Grid.GetColumn(text) == 0);
            Point? origin = lastId.TranslatePoint(default, trades);
            Assert.NotNull(origin);
            Assert.InRange(origin!.Value.Y + lastId.Bounds.Height, 0, trades.Viewport.Height + LayoutTolerance);
            AssertColumnsAligned(trades, tradeItems);

            // Pointer exit must not collapse either track into an overlay.
            window.MouseMove(new Point(1, 1), RawInputModifiers.None);
            Render();
            AssertOverflowGeometry(metrics);
            AssertOverflowGeometry(trades);

            Publish(vm, 1);
            Render();
            Assert.Single(vm.Trades);
            Assert.Single(tradeItems.ItemsSource!.Cast<BacktestTradeRow>());
            Assert.InRange(trades.Offset.Y, 0, trades.ScrollBarMaximum.Y);
            Assert.InRange(trades.Offset.X, 0, trades.ScrollBarMaximum.X);
            Assert.False(Bar(trades, Orientation.Vertical).IsVisible);
            Assert.Equal(1, vm.Trades[0].TradeId);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void EmptyAndNullDataContexts_KeepHeadersAndBoundedBoxesWithoutSyntheticRows()
    {
        var (window, view, vm) = Mount(1200);
        try
        {
            Publish(vm, 0);
            Render();
            Assert.Empty(Find<ItemsControl>(view, "Trades").ItemsSource!.Cast<BacktestTradeRow>());
            Assert.False(Bar(Find<ScrollViewer>(view, "TradesScroll"), Orientation.Vertical).IsVisible);
            Assert.NotEmpty(Find<ScrollViewer>(view, "TradesScroll").GetVisualDescendants()
                .OfType<TextBlock>().Where(text => text.Classes.Contains("ColumnHeader")));
            view.DataContext = null;
            Render();
            Assert.True(Find<Border>(view, "DetailsBlock").IsVisible);
            Assert.Null(Find<ItemsControl>(view, "Trades").ItemsSource);
            Assert.Null(Find<Button>(view, "ExportButton").Command);
            Assert.Equal(ViewportHeight, Find<ScrollViewer>(view, "MetricsScroll").Bounds.Height);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void FontsAndBrushes_UpdateWithoutRepublishing_AndNumbersRemainRightAligned()
    {
        var (window, view, vm) = Mount(1200);
        IResourceDictionary resources = Application.Current!.Resources;
        var changes = new Dictionary<string, object>
        {
            ["TitleFontSize"] = 27d,
            ["BaseFontSize"] = 23d,
            ["DetailFontSize"] = 19d,
            ["HelperFontSize"] = 17d,
            ["Brush.Border.Primary"] = Brushes.Magenta,
            ["Brush.Background.Secondary"] = Brushes.Navy,
        };
        var previous = changes.Keys.ToDictionary(key => key,
            key => (Present: resources.TryGetValue(key, out object? value), Value: value));
        try
        {
            Publish(vm, 1);
            long revision = vm.ResultRevision;
            foreach (var change in changes) resources[change.Key] = change.Value;
            Render();
            Border card = Find<Border>(view, "DetailsBlock");
            Assert.Equal(Brushes.Navy, card.Background);
            Assert.Equal(Brushes.Magenta, Find<Border>(view, "DetailsSeparator").Background);
            Assert.All(card.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.Classes.Contains("ResultsSectionTitle")),
                text => Assert.Equal(27d, text.FontSize));
            Assert.All(card.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.Classes.Contains("ColumnHeader")),
                text => Assert.Equal(23d, text.FontSize));
            TextBlock[] numeric = card.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.Classes.Contains("NumericValue")).ToArray();
            Assert.NotEmpty(numeric);
            Assert.All(numeric, text =>
            {
                Assert.Equal(19d, text.FontSize);
                Assert.Equal(TextAlignment.Right, text.TextAlignment);
                Assert.Equal(Application.Current.FindResource("Font_Mono"), text.FontFamily);
            });
            Assert.Equal(revision, vm.ResultRevision);
            Assert.Equal(ViewportHeight, Find<ScrollViewer>(view, "MetricsScroll").Bounds.Height);
        }
        finally
        {
            foreach (var item in previous)
            {
                if (item.Value.Present) resources[item.Key] = item.Value.Value!;
                else resources.Remove(item.Key);
            }
            window.Close();
        }
    }

    [AvaloniaFact]
    public void KeyboardAndWheel_ReachLocalDataAndChainToOuterResultsAtTheLimit()
    {
        var (window, view, vm) = Mount(800);
        try
        {
            window.Height = 600;
            Publish(vm, OverflowTradeCount);
            Render();
            ScrollViewer metrics = Find<ScrollViewer>(view, "MetricsScroll");
            ScrollViewer trades = Find<ScrollViewer>(view, "TradesScroll");
            ScrollViewer outer = trades.GetVisualAncestors().OfType<ScrollViewer>().Single();
            outer.Offset = outer.ScrollBarMaximum;
            Render();
            Assert.True(trades.Focus());
            window.KeyPressQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
            Render();
            Assert.True(trades.Offset.Y > 0);
            Assert.Equal(default, metrics.Offset);
            window.KeyPressQwerty(PhysicalKey.PageUp, RawInputModifiers.None);
            window.KeyReleaseQwerty(PhysicalKey.PageUp, RawInputModifiers.None);
            Render();
            Assert.Equal(0, trades.Offset.Y);
            Assert.True(ScrollViewer.GetIsScrollChainingEnabled(trades));
            // At its upper boundary, a wheel-up must reach the outer Results scroller.
            Point position = trades.TranslatePoint(new Point(20, 20), window)!.Value;
            double oldOuter = outer.Offset.Y;
            window.MouseWheel(position, new Vector(0, 1), RawInputModifiers.None);
            Render();
            Assert.True(outer.Offset.Y < oldOuter);
            Assert.Equal(0, trades.Offset.Y);
        }
        finally { window.Close(); }
    }

    private static void AssertColumnsAligned(ScrollViewer viewer, ItemsControl items)
    {
        StackPanel table = Assert.IsType<StackPanel>(viewer.Content);
        Grid header = Assert.IsType<Grid>(table.Children[0]);
        Grid row = items.GetVisualDescendants().OfType<Grid>()
            .First(grid => grid.DataContext is BacktestTradeRow or BacktestMetricDisplayRow &&
                grid.ColumnDefinitions.Count == header.ColumnDefinitions.Count);
        Assert.Equal(header.ColumnDefinitions.Count, row.ColumnDefinitions.Count);
        for (int i = 0; i < header.ColumnDefinitions.Count; i++)
            Assert.InRange(Math.Abs(header.ColumnDefinitions[i].ActualWidth - row.ColumnDefinitions[i].ActualWidth),
                0, LayoutTolerance);
    }

    private static void AssertOverflowGeometry(ScrollViewer viewer)
    {
        ScrollContentPresenter presenter = viewer.GetVisualDescendants().OfType<ScrollContentPresenter>()
            .Single(control => ReferenceEquals(control.TemplatedParent, viewer));
        Rect content = RectInViewer(presenter, viewer);
        foreach (Orientation direction in new[] { Orientation.Vertical, Orientation.Horizontal })
        {
            ScrollBar bar = Bar(viewer, direction);
            Assert.True(bar.IsVisible);
            Assert.False(bar.AllowAutoHide);
            Assert.True(bar.IsExpanded);
            Rect overlap = content.Intersect(RectInViewer(bar, viewer));
            Assert.True(overlap.Width <= 0 || overlap.Height <= 0, $"Scrollbar overlaps data: {overlap}");
            Assert.True(direction == Orientation.Vertical ? bar.Bounds.Width > 0 : bar.Bounds.Height > 0);
        }
    }

    private static Rect RectInViewer(Control child, ScrollViewer viewer) =>
        new(child.TranslatePoint(default, viewer)!.Value, child.Bounds.Size);

    private static ScrollBar Bar(ScrollViewer viewer, Orientation orientation) =>
        viewer.GetVisualDescendants().OfType<ScrollBar>().Single(bar =>
            ReferenceEquals(bar.TemplatedParent, viewer) && bar.Orientation == orientation);

    private static T Find<T>(Control view, string suffix) where T : Control =>
        view.GetVisualDescendants().OfType<T>().Single(control =>
            AutomationProperties.GetAutomationId(control) == Prefix + suffix);

    private static (Window Window, BacktestResultsView View, BacktestResultsViewModel Vm) Mount(double width)
    {
        var vm = new BacktestResultsViewModel(NullLocalizationService.Instance,
            Mock.Of<IBacktestReportExporter>(), Mock.Of<IDialogService>());
        var view = new BacktestResultsView { DataContext = vm };
        var window = new Window { Content = view, Width = width, Height = 900, UseLayoutRounding = false };
        window.Show();
        Render();
        return (window, view, vm);
    }

    private static void Publish(BacktestResultsViewModel vm, int tradeCount)
    {
        var (baseline, _) = BacktestResultsViewInteractionTests.MakeRun(110m);
        var trades = ImmutableArray.CreateBuilder<BacktestTrade>(tradeCount);
        DateTime start = baseline.EquityPoints[0].Timestamp;
        for (int i = 0; i < tradeCount; i++)
            trades.Add(new BacktestTrade(i + 1, TradeSide.Long,
                0, start, 100m, 1, start.AddDays(1), 110m,
                1m, 10m, 10m, 0m, 0m, 1, false));
        var result = new BacktestResult(baseline.Orders, baseline.Fills, trades.MoveToImmutable(),
            baseline.EquityPoints, baseline.Signals, baseline.Configuration, baseline.Status,
            baseline.StrategyName, baseline.ReproducibilityHash, baseline.IsInsufficientData);
        var report = new BacktestReportGenerator().Generate(result,
            new BacktestReportOptions(TimeFrame.D1, 0, start, start.AddDays(2)) { AnnualPeriods = 252 });
        vm.Update(result, report, 0);
    }

    private static void Render()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}

