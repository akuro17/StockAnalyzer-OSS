using System.Linq;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using StockAnalyzer.Avalonia.ViewModels.Screener;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Core.Models.Screener;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The Filters condition tree's group row: a list without conditions has no expression to show, so its row must not open a tooltip (an invisible content would
/// still open an empty popup), while the context menu of the row keeps its own tooltips. Mirrors the Backtest condition tree (BacktestConditionTreeViewTests).
/// </summary>
public class ScreenerConditionTreeTooltipTests
{
    private static void Render()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void EmptyList_HasNoRowTooltip_AndTheContextMenuKeepsItsOwn()
    {
        var screener = new ScreenerWindow();
        var template = screener.DataTemplates.OfType<TreeDataTemplate>().Single(t => t.DataType == typeof(ScreenerConditionGroupViewModel));
        var tree = new ScreenerConditionTreeViewModel(maxDepth: 4, maxNodes: 6, maxNameLength: 16);
        var row = (Border)template.Build(tree.Root)!;
        row.DataContext = tree.Root;
        var host = new Window { Content = row, Width = 400, Height = 100 };

        try
        {
            host.Show();
            Render();

            Assert.False(ToolTip.GetServiceEnabled(row));
            Assert.True(ToolTip.GetServiceEnabled(row.ContextMenu!));

            tree.TryAddLeaf(new ScreenerIndicatorEntry());
            Render();
            Assert.True(ToolTip.GetServiceEnabled(row));

            tree.Delete(tree.Root.Children[0]);
            Render();
            Assert.False(ToolTip.GetServiceEnabled(row));
        }
        finally
        {
            host.Close();
            screener.Close();
        }
    }
}
