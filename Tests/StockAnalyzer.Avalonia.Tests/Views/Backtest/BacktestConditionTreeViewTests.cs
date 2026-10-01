using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Moq;
using StockAnalyzer.Avalonia.Converters;
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
/// The Results tab's "Registered Conditions" section: three stacked TreeViews (Entry / Exit / Reverse) whose top-level items are the fixed Long and Short roots.
/// Behavior of the commands is covered by the view-model tests; this proves the view binds to them (MP-2 Phase 3).
/// </summary>
public class BacktestConditionTreeViewTests
{
    private sealed class EmptyCatalogProvider : IScreenerCatalogProvider
    {
        public IReadOnlyList<ScreenerCatalogItem> GetCatalogItems(IIndicatorFactory? indicatorFactory = null) => new List<ScreenerCatalogItem>();
        public CoreIndicatorSettings? GetDefaultSettings(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => null;
        public IReadOnlyList<string> GetOutputSeriesNames(IndicatorType type, IIndicatorFactory? indicatorFactory = null) => new[] { "Main" };
    }

    private static BacktestConditionEntry Entry(PriceType left, PriceType right) => new()
    {
        Left = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = left },
        Operator = ComparisonOperator.GreaterThan,
        TargetMode = RightHandTargetMode.Indicator,
        Right = new BacktestConditionSide { IndicatorType = IndicatorType.Price, PriceSource = right },
    };

    private static void Render()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static string? Id(global::Avalonia.Controls.Control control) => global::Avalonia.Automation.AutomationProperties.GetAutomationId(control);

    private static (BacktestIndicatorSelectionViewModel Selection, BacktestResultsView View, Window Window) Show()
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
        window.Show();
        Render();
        return (selection, view, window);
    }

    private static List<TextBlock> TextsWithId(BacktestResultsView view, string id)
        => view.GetVisualDescendants().OfType<TextBlock>().Where(t => Id(t) == id && t.IsEffectivelyVisible).ToList();

    [AvaloniaFact]
    public void ThreeTreeViews_ShowTheFixedLongAndShortRoots()
    {
        (_, BacktestResultsView view, Window window) = Show();
        try
        {
            var trees = view.GetVisualDescendants().OfType<TreeView>().Where(t => Id(t)?.StartsWith("Backtest_ConditionTree_") == true).ToList();

            Assert.Equal(new[] { "Backtest_ConditionTree_Entry", "Backtest_ConditionTree_Exit", "Backtest_ConditionTree_Reverse" }, trees.Select(Id));
            Assert.All(trees, tree => Assert.Equal(2, tree.GetVisualDescendants().OfType<TreeViewItem>().Count()));
            // Every root starts as an empty AND group.
            Assert.Equal(6, TextsWithId(view, "Backtest_ConditionTree_Operator_And").Count);
        }
        finally
        {
            window.Close();
        }
    }

    private static List<Border> GroupRows(BacktestResultsView view)
        => view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("ConditionRow") && b.DataContext is BacktestConditionGroupViewModel).ToList();

    /// <summary>
    /// A list without conditions has no expression to show, so its row must not open a tooltip at all (an invisible content would still open an empty popup;
    /// the opening itself was verified with real pointer input and a zero show delay, which a headless test cannot do reliably because of the frozen clock).
    /// The tooltips that belong to controls inside the row (the Reverse hint, the context menu items) stay available.
    /// </summary>
    [AvaloniaFact]
    public void EmptyLists_HaveNoRowTooltip_AndTheOwnTooltipsInsideTheRowRemain()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            List<Border> rows = GroupRows(view);
            Assert.Equal(6, rows.Count);
            Assert.All(rows, row => Assert.False(ToolTip.GetServiceEnabled(row)));
            Assert.All(rows, row => Assert.True(ToolTip.GetServiceEnabled(row.ContextMenu!)));

            List<TextBlock> reverseHints = view.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.DataContext is BacktestConditionGroupViewModel { IsReverseRoot: true } && ToolTip.GetTip(text) is string { Length: > 0 })
                .ToList();
            Assert.Equal(2, reverseHints.Count);
            Assert.All(reverseHints, hint => Assert.True(ToolTip.GetServiceEnabled(hint)));

            selection.ConditionTree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close));
            for (int i = 0; i < 3; i++) Render();

            BacktestConditionGroupViewModel entryLong = selection.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long);
            Assert.Single(GroupRows(view).Where(row => ToolTip.GetServiceEnabled(row)), row => ReferenceEquals(row.DataContext, entryLong));
            Assert.Equal(5, GroupRows(view).Count(row => !ToolTip.GetServiceEnabled(row)));
            Border leafRow = Assert.Single(view.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("ConditionRow") && b.DataContext is BacktestConditionLeafViewModel));
            Assert.True(ToolTip.GetServiceEnabled(leafRow));

            selection.ConditionTree.Delete(entryLong.Children[0]);
            for (int i = 0; i < 3; i++) Render();

            Assert.All(GroupRows(view), row => Assert.False(ToolTip.GetServiceEnabled(row)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AddedLeaf_AppearsUnderItsRoot_AndTheOperatorSwitchIsShown()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            Assert.Empty(TextsWithId(view, "Backtest_ConditionTree_Leaf"));

            selection.ConditionTree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close));
            Render();

            TextBlock leaf = Assert.Single(TextsWithId(view, "Backtest_ConditionTree_Leaf"));
            Assert.Equal(BacktestConditionFormatter.Format(Entry(PriceType.Open, PriceType.Close)), leaf.Text);

            selection.ConditionTree.UpdateGroup(selection.ConditionTree.Root(BacktestConditionSection.Entry, TradeSide.Long), null, LogicalOperator.Or);
            Render();

            Assert.Single(TextsWithId(view, "Backtest_ConditionTree_Operator_Or"));
            Assert.Equal(5, TextsWithId(view, "Backtest_ConditionTree_Operator_And").Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Selection_IsSharedAcrossTheThreeTrees_AndFollowsTheViewModel()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            BacktestConditionTreeViewModel tree = selection.ConditionTree;
            var items = view.GetVisualDescendants().OfType<TreeViewItem>().ToList();
            TreeViewItem entryLong = items.First(i => ReferenceEquals(i.DataContext, tree.Root(BacktestConditionSection.Entry, TradeSide.Long)));
            TreeViewItem exitShort = items.First(i => ReferenceEquals(i.DataContext, tree.Root(BacktestConditionSection.Exit, TradeSide.Short)));

            entryLong.SetCurrentValue(TreeViewItem.IsSelectedProperty, true);
            Render();
            Assert.Same(tree.Root(BacktestConditionSection.Entry, TradeSide.Long), tree.SelectedNode);

            tree.SelectedNode = tree.Root(BacktestConditionSection.Exit, TradeSide.Short);
            Render();

            Assert.True(exitShort.IsSelected);
            Assert.False(entryLong.IsSelected);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RowTooltips_ContainOnlyTheExpression_NoCaptionsOrExplanatoryText()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            selection.ConditionTree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close));
            Render();

            var rows = view.GetVisualDescendants().OfType<Border>().Where(b => b.ContextMenu is not null).ToList();
            Assert.NotEmpty(rows);
            foreach (Border row in rows)
            {
                object? tip = ToolTip.GetTip(row);
                var texts = tip switch
                {
                    TextBlock single => new List<TextBlock> { single },
                    StackPanel panel => panel.Children.OfType<TextBlock>().ToList(),
                    _ => new List<TextBlock>(),
                };
                // A leaf tip is one expression line; a group tip is at most the two expression lines (above / below), all of them TextBlocks.
                Assert.InRange(texts.Count, 1, 2);
                Assert.All(texts, t => Assert.StartsWith("Backtest_ConditionTree_Hover", Id(t)));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HoverTip_NeverWraps_AndItsToolTipHasNoWidthLimit()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            selection.ConditionTree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close));
            Render();

            foreach (Border row in view.GetVisualDescendants().OfType<Border>().Where(b => b.ContextMenu is not null).ToList())
            {
                var content = (Control)ToolTip.GetTip(row)!;
                var texts = content is TextBlock single ? new List<TextBlock> { single } : ((Panel)content).Children.OfType<TextBlock>().ToList();
                Assert.All(texts, t => Assert.Equal(global::Avalonia.Media.TextWrapping.NoWrap, t.TextWrapping));

                // Hosted the way the tooltip service hosts it, the theme's width limit is lifted for this tip only.
                var host = new ToolTip { Content = content, MaxWidth = 320 };
                var hostWindow = new Window { Content = host };
                hostWindow.Show();
                Render();
                Assert.True(double.IsPositiveInfinity(host.MaxWidth));
                hostWindow.Close();
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void GroupContextMenu_IsBoundToTheTreeCommands_AndExplainsADisabledAddGroup()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            BacktestConditionTreeViewModel tree = selection.ConditionTree;
            BacktestConditionGroupViewModel root = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);
            for (int i = 0; i < tree.MaxNodes - 1; i++) Assert.NotNull(tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close)));
            Render();

            TreeViewItem item = view.GetVisualDescendants().OfType<TreeViewItem>().First(i => ReferenceEquals(i.DataContext, root));
            Assert.Null(item.ContextMenu);
            Border row = item.GetVisualDescendants().OfType<Border>().First(b => b.ContextMenu is not null);
            ContextMenu menu = row.ContextMenu!;
            menu.Open(row);
            Render();

            List<MenuItem> menuItems = menu.Items.OfType<MenuItem>().ToList();
            MenuItem addGroup = menuItems.Single(m => Id(m) == "Backtest_ConditionTree_Menu_AddGroup");
            Assert.Same(tree.AddGroupCommand, addGroup.Command);
            Assert.Same(root, addGroup.CommandParameter);
            Assert.False(addGroup.IsEffectivelyEnabled);
            Assert.False(string.IsNullOrEmpty(root.AddGroupToolTip));
            Assert.Equal(root.AddGroupToolTip, ToolTip.GetTip(addGroup));
            Assert.Same(tree.EditGroupCommand, menuItems.Single(m => Id(m) == "Backtest_ConditionTree_Menu_EditGroup").Command);
            // The two "Add AND/OR group" items and "Switch AND/OR" are gone.
            Assert.DoesNotContain(menuItems, m => Id(m) is "Backtest_ConditionTree_Menu_AddAnd" or "Backtest_ConditionTree_Menu_AddOr" or "Backtest_ConditionTree_Menu_Switch");
            Assert.Same(tree.DeleteNodeCommand, menuItems.Single(m => Id(m) == "Backtest_ConditionTree_Menu_Clear").Command);
            menu.Close();
        }
        finally
        {
            window.Close();
        }
    }

    // ---- real pointer input (MP-B): select, add, delete and expand through the rendered rows ----

    private static global::Avalonia.Point Center(Window window, Control control)
        => control.TranslatePoint(new global::Avalonia.Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Control control, global::Avalonia.Input.MouseButton button)
    {
        // Pointer hit testing runs against the composed frame, which lags behind a layout change: let it settle first.
        for (int i = 0; i < 3; i++) Render();
        global::Avalonia.Point point = Center(window, control);
        window.MouseDown(point, button);
        try
        {
            window.MouseUp(point, button);
        }
        catch (System.InvalidOperationException ex) when (ex.Message.Contains("job loop"))
        {
            // The release has been processed; the headless platform's frozen clock cannot finish a running transition (context menu / expander) and reports a job loop.
        }
        Render();
    }

    private static void Drag(Window window, global::Avalonia.Point point, global::Avalonia.Input.Raw.RawDragEventType type, global::Avalonia.Input.IDataObject data)
    {
        try
        {
            window.DragDrop(point, type, data, global::Avalonia.Input.DragDropEffects.Move);
        }
        catch (System.InvalidOperationException ex) when (ex.Message.Contains("job loop"))
        {
            // The event has been processed; the headless platform's frozen clock cannot finish a running transition and reports a job loop.
        }
    }

    [AvaloniaFact]
    public void RealClicks_SelectAGroup_AddALeafIntoIt_ThenDeleteTheLeaf_RemovesTheEmptiedGroup()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            BacktestConditionTreeViewModel tree = selection.ConditionTree;
            BacktestConditionGroupViewModel root = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);
            Assert.True(tree.TryAddGroup(root, LogicalOperator.Or, "MA"));
            var group = (BacktestConditionGroupViewModel)root.Children[0];
            Render();

            // Left click on the group row selects it; the add target then is that group (same section and side).
            Click(window, TextsWithId(view, "Backtest_ConditionTree_GroupName").Single(), global::Avalonia.Input.MouseButton.Left);
            Assert.Same(group, tree.SelectedNode);
            Assert.Same(group, tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close)));
            Render();
            Assert.Single(group.Children);

            // Right click on the leaf row selects that leaf, and the menu's Delete command targets it.
            TextBlock leafText = TextsWithId(view, "Backtest_ConditionTree_Leaf").Single();
            Click(window, leafText, global::Avalonia.Input.MouseButton.Right);
            BacktestConditionNodeViewModel leaf = group.Children[0];
            Assert.Same(leaf, tree.SelectedNode);

            Border row = leafText.FindAncestorOfType<Border>()!;
            MenuItem delete = row.ContextMenu!.Items.OfType<MenuItem>().Single(m => Id(m) == "Backtest_ConditionTree_Menu_DeleteLeaf");
            Assert.Same(leaf, delete.CommandParameter);
            Assert.True(delete.Command!.CanExecute(delete.CommandParameter));
            delete.Command.Execute(delete.CommandParameter);
            Render();

            // The group the delete emptied is removed with it; the fixed root stays.
            Assert.Empty(root.Children);
            Assert.Null(tree.SelectedNode);
            Assert.Empty(TextsWithId(view, "Backtest_ConditionTree_GroupName"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClickingTheExpandArrow_TogglesTheGroupOnce_WithoutChangingTheTreeStructure()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            BacktestConditionTreeViewModel tree = selection.ConditionTree;
            BacktestConditionGroupViewModel root = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);
            tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close));
            Render();
            int revision = tree.Revision;
            int structureChanges = 0;
            int expandedChanges = 0;
            tree.StructureChanged += (_, _) => structureChanges++;
            root.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(root.IsExpanded)) expandedChanges++; };
            Assert.True(root.IsExpanded);

            TreeViewItem item = view.GetVisualDescendants().OfType<TreeViewItem>().First(i => ReferenceEquals(i.DataContext, root));
            ToggleButton arrow = item.GetVisualDescendants().OfType<ToggleButton>().First();
            Click(window, arrow, global::Avalonia.Input.MouseButton.Left);

            Assert.False(root.IsExpanded);
            Assert.Equal(1, expandedChanges);
            Click(window, arrow, global::Avalonia.Input.MouseButton.Left);
            Assert.True(root.IsExpanded);
            Assert.Equal(2, expandedChanges);
            // Opening and closing never rebuilds the expressions nor counts as an edit.
            Assert.Equal(0, structureChanges);
            Assert.Equal(revision, tree.Revision);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RightClickOnARow_SelectsIt_WithoutTogglingItsExpansion()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            BacktestConditionTreeViewModel tree = selection.ConditionTree;
            BacktestConditionGroupViewModel root = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);
            tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close));
            Render();
            TreeViewItem item = view.GetVisualDescendants().OfType<TreeViewItem>().First(i => ReferenceEquals(i.DataContext, root));
            Border row = item.GetVisualDescendants().OfType<Border>().First(b => b.ContextMenu is not null);

            Click(window, row, global::Avalonia.Input.MouseButton.Right);

            Assert.Same(root, tree.SelectedNode);
            Assert.True(root.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }

    // ---- moving a comparison by drag and drop (MP-C) ----

    [Theory]
    [InlineData(0, 0, 4, 0, true)]
    [InlineData(0, 0, 3, 3, false)]
    [InlineData(0, 0, 0, -4, true)]
    [InlineData(10, 10, 13, 13, false)]
    public void DragStartsOnceThePointerHasTravelledTheConfiguredDistance_OnEitherAxis(double x0, double y0, double x1, double y1, bool expected)
    {
        Assert.Equal(expected, ConditionDragGesture.HasMovedFarEnough(new global::Avalonia.Point(x0, y0), new global::Avalonia.Point(x1, y1), 4));
    }

    [AvaloniaFact]
    public void DropPlacement_IsInsideForAGroupRow_AndBeforeOrAfterForAComparisonRowByItsHalf()
    {
        (BacktestIndicatorSelectionViewModel selection, _, Window window) = Show();
        try
        {
            BacktestConditionTreeViewModel tree = selection.ConditionTree;
            tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close));
            BacktestConditionGroupViewModel root = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);

            Assert.Equal(ConditionDropPlacement.Inside, ConditionDragGesture.PlacementFor(root, 1, 20));
            Assert.Equal(ConditionDropPlacement.Inside, ConditionDragGesture.PlacementFor(root, 19, 20));
            Assert.Equal(ConditionDropPlacement.Before, ConditionDragGesture.PlacementFor(root.Children[0], 9.9, 20));
            Assert.Equal(ConditionDropPlacement.After, ConditionDragGesture.PlacementFor(root.Children[0], 10, 20));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DroppingAConditionOnTheLowerHalfOfAnotherRow_MovesItBehindThatRow_AndTheIndicatorIsCleared()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            BacktestConditionTreeViewModel tree = selection.ConditionTree;
            BacktestConditionGroupViewModel root = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);
            tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close));
            tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.High, PriceType.Low));
            Render();
            Render();
            var first = (BacktestConditionLeafViewModel)root.Children[0];
            var second = (BacktestConditionLeafViewModel)root.Children[1];
            TreeViewItem secondItem = view.GetVisualDescendants().OfType<TreeViewItem>().First(i => ReferenceEquals(i.DataContext, second));
            Border row = secondItem.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(ConditionDragGesture.RowClass));
            global::Avalonia.Point lowerHalf = row.TranslatePoint(new global::Avalonia.Point(row.Bounds.Width / 2, row.Bounds.Height * 0.75), window)!.Value;

            #pragma warning disable CS0618
            var data = new global::Avalonia.Input.DataObject();
            #pragma warning restore CS0618
            data.Set(BacktestResultsView.ConditionLeafFormat, first);
            Drag(window, lowerHalf, global::Avalonia.Input.Raw.RawDragEventType.DragEnter, data);
            Drag(window, lowerHalf, global::Avalonia.Input.Raw.RawDragEventType.DragOver, data);
            Assert.Contains(ConditionDragGesture.DropAfterClass, row.Classes);
            // Pointer routing runs against the composed frame: let it settle between the events, as a real drag does.
            for (int i = 0; i < 3; i++) Render();
            Drag(window, lowerHalf, global::Avalonia.Input.Raw.RawDragEventType.Drop, data);
            Render();

            Assert.Same(second, root.Children[0]);
            Assert.Same(first, root.Children[1]);
            Assert.DoesNotContain(ConditionDragGesture.DropAfterClass, row.Classes);
            Assert.Same(first, tree.SelectedNode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DroppingAConditionOnAnotherRoot_IsNotAccepted_AndChangesNothing()
    {
        (BacktestIndicatorSelectionViewModel selection, BacktestResultsView view, Window window) = Show();
        try
        {
            BacktestConditionTreeViewModel tree = selection.ConditionTree;
            BacktestConditionGroupViewModel entryLong = tree.Root(BacktestConditionSection.Entry, TradeSide.Long);
            BacktestConditionGroupViewModel entryShort = tree.Root(BacktestConditionSection.Entry, TradeSide.Short);
            tree.TryAddLeaf(BacktestConditionSection.Entry, TradeSide.Long, Entry(PriceType.Open, PriceType.Close));
            Render();
            Render();
            int revision = tree.Revision;
            TreeViewItem shortItem = view.GetVisualDescendants().OfType<TreeViewItem>().First(i => ReferenceEquals(i.DataContext, entryShort));
            Border row = shortItem.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains(ConditionDragGesture.RowClass));
            global::Avalonia.Point center = row.TranslatePoint(new global::Avalonia.Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;

            #pragma warning disable CS0618
            var data = new global::Avalonia.Input.DataObject();
            #pragma warning restore CS0618
            data.Set(BacktestResultsView.ConditionLeafFormat, entryLong.Children[0]);
            Drag(window, center, global::Avalonia.Input.Raw.RawDragEventType.DragEnter, data);
            Drag(window, center, global::Avalonia.Input.Raw.RawDragEventType.DragOver, data);
            Assert.DoesNotContain(ConditionDragGesture.DropInsideClass, row.Classes);
            Drag(window, center, global::Avalonia.Input.Raw.RawDragEventType.Drop, data);
            Render();

            Assert.Single(entryLong.Children);
            Assert.Empty(entryShort.Children);
            Assert.Equal(revision, tree.Revision);
        }
        finally
        {
            window.Close();
        }
    }
}
