using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.ViewModels.TickerList;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Models.Watchlist;
using Xunit;
using StockAnalyzer.Avalonia.Tests.TestHelpers;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// The Training Settings tab of the Training Wizard: the ticker lists are a checkbox tree in the left column, the Data Period fields are stacked in the
/// right column, and the calendar pickers draw an icon. Real window, real view-model, a scripted ticker store.
/// </summary>
public sealed class TrainingWizardTickerListWindowTests
{
    private static void Pump()
    {
        for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private static (TrainingWizardWindow Window, TrainingWizardViewModel Vm) Mount(int watchlists)
    {
        var store = new FakeTickerStateStore();
        store.Groups.Add(new AllTickersNode("All"));
        var category = new CategoryNode(TickerListViewModel.WatchlistsCategoryId, "Watchlists");
        for (int i = 0; i < watchlists; i++) category.Children!.Add(new WatchlistNode(new WatchlistProfile(Guid.NewGuid(), "W" + i, IndicatorColor.Gray, false)));
        store.Groups.Add(category);
        var vm = new TrainingWizardViewModel(tickerStore: store);
        var window = new TrainingWizardWindow { DataContext = vm };
        window.Show();
        Pump(); Pump();
        return (window, vm);
    }

    private static double Top(Visual visual, Visual relativeTo) => visual.TranslatePoint(new global::Avalonia.Point(0, 0), relativeTo)!.Value.Y;

    [AvaloniaFact]
    public void TheTickerListsAreATreeOnTheLeftAndTheDataPeriodIsStackedOnTheRight()
    {
        var (window, vm) = Mount(3);
        try
        {
            var tree = window.GetVisualDescendants().OfType<TreeView>().Single(t => t.Name == "TickerTree");
            var grid = tree.GetVisualAncestors().OfType<Grid>().First(g => g.ColumnDefinitions.Count == 2 && g.ColumnDefinitions.All(c => c.Width.IsStar));
            Assert.All(grid.ColumnDefinitions, c => Assert.True(c.Width.IsStar));
            var left = grid.Children.Single(c => Grid.GetColumn(c) == 0); var right = grid.Children.Single(c => Grid.GetColumn(c) == 1);
            Assert.Contains(tree, left.GetVisualDescendants());
            var pickers = right.GetVisualDescendants().OfType<CalendarDatePicker>().ToArray();
            Assert.Equal(2, pickers.Length);
            var timeframe = right.GetVisualDescendants().OfType<ComboBox>().First();
            Assert.True(Top(pickers[0], window) < Top(pickers[1], window));                        // start above end
            Assert.True(Top(pickers[1], window) < Top(timeframe, window));                         // then the timeframe, all in one column
            Assert.Equal(pickers[0].Bounds.Width, pickers[1].Bounds.Width, 0.5);
            Assert.Equal(left.Bounds.Bottom, right.Bounds.Bottom, 0.5);                            // both cards end together
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<RadioButton>(), r => r.GroupName == "TrainingScope");     // the scope radio buttons are gone
            // The rows: a checkbox per list, "All tickers" ticked, one tick switches the view-model item.
            var boxes = tree.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsVisible).ToArray();
            Assert.Equal(5, boxes.Length);                                                         // All + Watchlists + 3 lists
            Assert.True(boxes[0].IsChecked);
            boxes[2].IsChecked = true;
            Assert.True(vm.SourceTree[1].Children[0].IsSelected);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheTreeScrollsInsideAPermanentScrollbarOnlyBeyondTheVisibleRows()
    {
        var (many, _) = Mount(14);                                       // 16 rows
        try
        {
            var tree = many.GetVisualDescendants().OfType<TreeView>().Single(t => t.Name == "TickerTree");
            var scroll = tree.GetVisualAncestors().OfType<ScrollViewer>().First();
            var theme = (global::Avalonia.Styling.ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!;
            Assert.Same(theme, scroll.Theme);
            Assert.False(scroll.AllowAutoHide);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, $"extent {scroll.Extent.Height} viewport {scroll.Viewport.Height}");
            Assert.True(scroll.Bounds.Height <= TrainingWizardUiLayout.SourceListMaxHeight + 0.5, $"bounds {scroll.Bounds.Height}");
            Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);                  // Auto: the bar appears exactly when the content is taller than the viewport
            Assert.NotEmpty(tree.GetVisualDescendants().OfType<PathIcon>().Where(i => i.IsVisible && i.Data is not null));       // watchlist rows carry the ticker tab's icon
        }
        finally { many.Close(); }
        var (few, _) = Mount(2);
        try
        {
            var scroll = few.GetVisualDescendants().OfType<TreeView>().Single(t => t.Name == "TickerTree").GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5);
        }
        finally { few.Close(); }
    }

    [AvaloniaFact]
    public void TheCalendarPickersDrawTheSharedIconInsteadOfADayNumber()
    {
        var (window, _) = Mount(1);
        try
        {
            foreach (var picker in window.GetVisualDescendants().OfType<CalendarDatePicker>())
            {
                var button = picker.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Button");
                var icon = Assert.Single(button.GetVisualDescendants().OfType<PathIcon>());
                Assert.NotNull(icon.Data);
            }
        }
        finally { window.Close(); }
    }
}
