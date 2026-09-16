using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// sa_improve (per the common UI spec for layout-width optimization and scrollbar display control):
/// TrainingWizardWindow's body ScrollViewer already used <c>SidebarScrollViewerTheme</c> +
/// <c>AllowAutoHide="False"</c>, but the Scope pick-lists (Watchlist / Portfolio), the progress
/// log <c>ListBox</c>, and the Per-Fold Results table still inherited Avalonia FluentTheme's
/// Overlay/Auto-hide scrollbar (collapses to near-invisible outside hover, floats over content).
/// These checks mount the real window and assert, at the element-property level (unconditional in
/// XAML, so no overflow/visibility dependence), that every scroll region now presents a permanent
/// standard-width scrollbar and that the log / fold frames overflow-scroll inside their card
/// rather than clipping.
/// </summary>
public class TrainingWizardWindowScrollSpecTests
{
    private static (TrainingWizardWindow window, TrainingWizardViewModel vm) Mount()
    {
        var vm = new TrainingWizardViewModel();
        vm.LogLines.Add("STDERR: sample progress line long enough to exceed a narrow card width");
        vm.FoldResults.Add(new FoldMetricRow
        {
            Fold = 0, Splits = 5, SampleCount = 120,
            Accuracy = 0.54, BaselineAccuracy = 0.51, MacroF1 = 0.33, MultiLogloss = 1.02,
        });
        vm.FoldResults.Add(new FoldMetricRow
        {
            Fold = 4, Splits = 5, SampleCount = 118,
            Accuracy = 0.57, BaselineAccuracy = 0.51, MacroF1 = 0.36, MultiLogloss = 0.96,
            IsHoldout = true,
        });

        var window = new TrainingWizardWindow { DataContext = vm };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    [AvaloniaFact]
    public void ScopePickLists_UseNonOverlayThemeAndStayAlwaysVisible()
    {
        var (window, vm) = Mount();
        try
        {
            vm.SelectedScope = TrainingScopeKind.Watchlist;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var expectedTheme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!;
            var pickList = window.GetVisualDescendants().OfType<ItemsControl>()
                .Single(ic => ReferenceEquals(ic.ItemsSource, vm.WatchlistProfiles));
            var scrollViewer = pickList.GetVisualAncestors().OfType<ScrollViewer>().First();

            Assert.Same(expectedTheme, scrollViewer.Theme);
            Assert.False(scrollViewer.AllowAutoHide,
                "the Watchlist pick-list scrollbar must stay permanently visible (AllowAutoHide=False)");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ProgressLog_ListBox_HasPermanentNonOverlayScrollbarsAndHorizontalOverflow()
    {
        var (window, vm) = Mount();
        try
        {
            var logBox = window.GetVisualDescendants().OfType<ListBox>()
                .Single(lb => ReferenceEquals(lb.ItemsSource, vm.LogLines));

            Assert.False(ScrollViewer.GetAllowAutoHide(logBox),
                "the progress-log scrollbar must not auto-hide");
            Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetHorizontalScrollBarVisibility(logBox));
            Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(logBox));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void BodyContent_IsCappedToTheViewport_WithEqualLeftRightGutters()
    {
        var (window, _) = Mount();
        try
        {
            var body = window.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(sv => sv.Name == "BodyScroll");

            // The single Border wrapping the tab content (the only Border under BodyScroll with a
            // TabControl in its subtree).
            var contentBorder = body.GetVisualDescendants().OfType<Border>()
                .First(b => b.GetVisualDescendants().OfType<TabControl>().Any());

            // Symmetric, non-zero horizontal padding is the "same right margin as the Scope tab's
            // left" the fix must guarantee - neither is implied by the selector above.
            Assert.Equal(contentBorder.Padding.Left, contentBorder.Padding.Right);
            Assert.True(contentBorder.Padding.Left > 0, "the wrapper Border must add a real left/right gutter");

            // Capped to the viewport, so the star-sized settings rows can no longer lay out wider
            // than the visible area and slide under the permanent vertical scrollbar.
            Assert.True(body.Viewport.Width > 0, "viewport should be measured after layout");
            Assert.True(contentBorder.Bounds.Width <= body.Viewport.Width + 0.5,
                $"body content ({contentBorder.Bounds.Width}) must stay within the viewport ({body.Viewport.Width})");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PerFoldResults_Table_OverflowScrollsHorizontallyInsteadOfClipping()
    {
        var (window, vm) = Mount();
        try
        {
            var foldItems = window.GetVisualDescendants().OfType<ItemsControl>()
                .Single(ic => ReferenceEquals(ic.ItemsSource, vm.FoldResults));

            var tablePanel = foldItems.GetVisualAncestors().OfType<StackPanel>()
                .First(sp => sp.MinWidth > 0);
            // The min width is defined once as the TrainingWizardFoldTableMinWidth window resource;
            // resolve it from the mounted window instead of restating the literal here.
            var foldTableMinWidth = Assert.IsType<double>(window.FindResource("TrainingWizardFoldTableMinWidth"));
            Assert.Equal(foldTableMinWidth, tablePanel.MinWidth);

            var hScroller = foldItems.GetVisualAncestors().OfType<ScrollViewer>().First();
            Assert.Equal(ScrollBarVisibility.Auto, hScroller.HorizontalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, hScroller.VerticalScrollBarVisibility);
            Assert.False(hScroller.AllowAutoHide,
                "the Per-Fold Results horizontal scrollbar must stay permanently visible");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PerFoldResults_HoldoutRow_ShowsBadgeThatReferenceRowsDoNotShow()
    {
        var (window, vm) = Mount();
        try
        {
            var rows = window.GetVisualDescendants().OfType<Grid>()
                .Where(g => g.Classes.Contains("foldRow"))
                .ToList();
            Assert.Equal(vm.FoldResults.Count, rows.Count);

            foreach (var row in rows)
            {
                var metric = Assert.IsType<FoldMetricRow>(row.DataContext);
                var foldColumn = row.Children.OfType<StackPanel>().Single(sp => Grid.GetColumn(sp) == 0);
                var badge = Assert.IsType<TextBlock>(foldColumn.Children[1]);

                // IsHoldout is the sole source of truth (FoldMetricRow); the badge only mirrors it,
                // so a reference fold must never show it and the holdout fold must always show it.
                Assert.Equal(metric.IsHoldout, badge.IsVisible);
            }

            Assert.Contains(rows, r => ((FoldMetricRow)r.DataContext!).IsHoldout);
            Assert.Contains(rows, r => !((FoldMetricRow)r.DataContext!).IsHoldout);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void BodyContentWrapper_HasHitTestableTransparentBackground()
    {
        var (window, _) = Mount();
        try
        {
            var body = window.GetVisualDescendants().OfType<ScrollViewer>()
                .Single(sv => sv.Name == "BodyScroll");
            var contentBorder = body.GetVisualDescendants().OfType<Border>()
                .First(b => b.GetVisualDescendants().OfType<TabControl>().Any());

            // A null Background makes the wrapper transparent to hit-testing, so a wheel gesture over
            // a blank gap inside it would not route to the body ScrollViewer's wheel-redirect
            // (SA_UI_INTERACTION.md sections 25 and 28). Transparent is painted-but-invisible and
            // stays hit-testable.
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(contentBorder.Background);
            Assert.Equal(Colors.Transparent, brush.Color);
        }
        finally
        {
            window.Close();
        }
    }
}
