using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Tests.Services;
using StockAnalyzer.Avalonia.ViewModels;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Avalonia.Views.Controls;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

/// <summary>sa_improve (共通UI仕様「UIレイアウト幅の最適化およびスクロールバーの表示制御仕様」準拠):
/// the sidebar's left icon column already used SidebarScrollViewerTheme + AllowAutoHide="False", but
/// the category-flyout tool list (DrawingToolSidebar.axaml) and the Objects/Layer Manager list
/// (DrawingObjectsView.axaml) were still plain ScrollViewers, inheriting Avalonia FluentTheme's
/// default Overlay/Auto-hide scrollbar (collapses to near-invisible outside of hover, floats over
/// content instead of a dedicated non-overlapping column). Mounts the real Views to confirm both now
/// wire up the same SidebarScrollViewerTheme (whose Grid separates content/scrollbar into distinct
/// columns - already proven non-overlapping by NoteTimelineView_ComposePanelTests and the theme
/// consolidation probe) with AllowAutoHide="False". Asserted directly on the ScrollViewer element
/// (Theme reference + AllowAutoHide property) rather than via rendered ScrollBar bounds, since the
/// latter requires the content to actually overflow and the ScrollBar to become Visible first - the
/// element-level properties are set unconditionally by XAML regardless of overflow state.</summary>
public class DrawingToolSidebarScrollSpecTests
{
    [AvaloniaFact]
    public void CategoryFlyout_ScrollViewer_UsesNonOverlayThemeAndStaysAlwaysVisible()
    {
        var dispatcher = new SynchronousDispatcherService();
        var chartVm = new ChartViewModel();
        var objectsVm = new DrawingObjectsViewModel(chartVm, dispatcher);
        var sidebarVm = new DrawingToolSidebarViewModel(chartVm, objectsVm);

        var view = new DrawingToolSidebar { DataContext = sidebarVm };
        var window = new Window { Content = view, Width = 400, Height = 400 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        try
        {
            sidebarVm.OpenCategory(sidebarVm.Categories[0]);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var toolsItemsControl = view.GetVisualDescendants().OfType<ItemsControl>()
                .Single(ic => ReferenceEquals(ic.ItemsSource, sidebarVm.ExpandedCategory!.Tools));

            var scrollViewer = toolsItemsControl.GetVisualAncestors().OfType<ScrollViewer>().Single();

            var expectedTheme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!;
            Assert.Same(expectedTheme, scrollViewer.Theme);
            Assert.False(scrollViewer.AllowAutoHide,
                "the category flyout's scrollbar must stay permanently visible (AllowAutoHide=False), not only on hover");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ObjectsList_ScrollViewer_UsesNonOverlayThemeAndStaysAlwaysVisible()
    {
        var dispatcher = new SynchronousDispatcherService();
        var chartVm = new ChartViewModel();
        var objectsVm = new DrawingObjectsViewModel(chartVm, dispatcher);

        var view = new DrawingObjectsView { DataContext = objectsVm };
        var window = new Window { Content = view, Width = 300, Height = 300 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var scrollViewer = view.GetVisualDescendants().OfType<ScrollViewer>().Single();

            var expectedTheme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!;
            Assert.Same(expectedTheme, scrollViewer.Theme);
            Assert.False(scrollViewer.AllowAutoHide,
                "the Objects List's scrollbar must stay permanently visible (AllowAutoHide=False), not only on hover");
        }
        finally
        {
            window.Close();
        }
    }
}
