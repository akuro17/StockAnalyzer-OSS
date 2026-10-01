using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using StockAnalyzer.Avalonia.Behaviors;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Core.Models.UI;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Behaviors;

public class TabReorderBehaviorMoveCommandTests
{
    private sealed class RecordingCommand : ICommand
    {
        private readonly bool _canExecute;
        public RecordingCommand(bool canExecute = true) => _canExecute = canExecute;
        public List<object?> Executed { get; } = new();
        public event System.EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => _canExecute;
        public void Execute(object? parameter) => Executed.Add(parameter);
    }

    private sealed class Harness
    {
        public Window Window { get; set; } = null!;
        public TabControl TabControl { get; set; } = null!;
        public int CloseClicks { get; set; }
    }

    [AvaloniaFact]
    public void RaiseMoveCommand_WithMoveCommand_ExecutesTypedRequestOnce()
    {
        var tabControl = new TabControl();
        var command = new RecordingCommand();
        TabReorderBehavior.SetMoveCommand(tabControl, command);

        TabReorderBehavior.RaiseMoveCommand(tabControl, 0, 2);

        var request = Assert.IsType<TabMoveRequest>(Assert.Single(command.Executed));
        Assert.Equal(new TabMoveRequest(0, 2), request);
    }

    [AvaloniaFact]
    public void RaiseMoveCommand_WhenCommandCannotExecute_DoesNotExecute()
    {
        var tabControl = new TabControl();
        var command = new RecordingCommand(canExecute: false);
        TabReorderBehavior.SetMoveCommand(tabControl, command);

        TabReorderBehavior.RaiseMoveCommand(tabControl, 0, 2);

        Assert.Empty(command.Executed);
    }

    [AvaloniaFact]
    public void RaiseMoveCommand_WithOnlyReorderCommand_IsNoOp()
    {
        var tabControl = new TabControl();
        var reorder = new RecordingCommand();
        TabReorderBehavior.SetReorderCommand(tabControl, reorder);

        TabReorderBehavior.RaiseMoveCommand(tabControl, 0, 2);

        Assert.Null(TabReorderBehavior.GetMoveCommand(tabControl));
        Assert.Empty(reorder.Executed);
    }

    // Tab headers mirror the Tab Window template: a close Button and a title TextBlock inside each tab.
    private static Harness ShowTabControl(bool behaviorEnabled = true, Dock placement = Dock.Top)
    {
        var harness = new Harness();
        var items = new ObservableCollection<WorkspaceViewItem> { TestWorkspaceItems.Create("A"), TestWorkspaceItems.Create("B"), TestWorkspaceItems.Create("C") };
        var closeCommand = new RelayCommand<WorkspaceViewItem>(_ => harness.CloseClicks++);

        var tabControl = new TabControl
        {
            TabStripPlacement = placement,
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<WorkspaceViewItem>((item, _) =>
            {
                var dock = new DockPanel { Background = Brushes.Transparent, MinWidth = 90 };
                var button = new Button { Command = closeCommand, CommandParameter = item, Width = 20, Height = 20, Content = "x" };
                DockPanel.SetDock(button, Dock.Right);
                dock.Children.Add(button);
                dock.Children.Add(new TextBlock { Text = item?.Title });
                return dock;
            })
        };
        TabReorderBehavior.SetIsEnabled(tabControl, behaviorEnabled);
        TabReorderBehavior.SetMoveCommand(tabControl, new RecordingCommand());
        TabReorderBehavior.SetReorderCommand(tabControl, new RecordingCommand());

        var window = new Window { Content = tabControl, Width = 600, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        harness.Window = window;
        harness.TabControl = tabControl;
        return harness;
    }

    private static void ClickAt(Window window, Point point)
    {
        // Pointer hit testing runs against the composed frame, which lags behind a layout change.
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        HeadlessDispatcher.Settle();
        window.MouseDownSettled(point, MouseButton.Left);
        window.MouseUpSettled(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point CenterOf(Control control, Window window) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

#pragma warning disable CS0618 // Mirrors TabReorderBehavior: DataObject / DragEventArgs.Data are obsolete in Avalonia 11.3.
    // Raised on the element under the pointer so the event bubbles up to the TabControl like a real drop
    // (RaiseEvent overwrites Source with the element it is called on).
    private static void RaiseDrop(TabControl tabControl, TabItem draggedTab, int sourceIndex, Interactive elementUnderPointer, Point? pointInTabControl = null)
    {
        var data = new DataObject();
        data.Set("TabItem", draggedTab);
        data.Set("SourceIndex", sourceIndex);
        var args = new DragEventArgs(DragDrop.DropEvent, data, tabControl, pointInTabControl ?? new Point(0, 0), KeyModifiers.None);
        elementUnderPointer.RaiseEvent(args);
    }
#pragma warning restore CS0618

    private static TextBlock TitleOf(TabItem tab) => tab.GetVisualDescendants().OfType<TextBlock>().First();

    [AvaloniaFact]
    public void Drop_OnMiddleTab_RaisesMoveCommandWithThatTabsIndex()
    {
        var harness = ShowTabControl();
        try
        {
            var tabs = harness.TabControl.GetVisualDescendants().OfType<TabItem>().ToList();
            var command = (RecordingCommand)TabReorderBehavior.GetMoveCommand(harness.TabControl)!;

            RaiseDrop(harness.TabControl, tabs[0], 0, TitleOf(tabs[1]));

            Assert.Equal(new TabMoveRequest(0, 1), Assert.IsType<TabMoveRequest>(Assert.Single(command.Executed)));
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Drop_OutsideAnyTab_RaisesMoveCommandWithLastIndex()
    {
        var harness = ShowTabControl();
        try
        {
            var tabs = harness.TabControl.GetVisualDescendants().OfType<TabItem>().ToList();
            var command = (RecordingCommand)TabReorderBehavior.GetMoveCommand(harness.TabControl)!;

            RaiseDrop(harness.TabControl, tabs[0], 0, harness.TabControl);

            Assert.Equal(new TabMoveRequest(0, 2), Assert.IsType<TabMoveRequest>(Assert.Single(command.Executed)));
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Drop_OnTheDraggedTabItself_DoesNotRaiseMoveCommand()
    {
        var harness = ShowTabControl();
        try
        {
            var tabs = harness.TabControl.GetVisualDescendants().OfType<TabItem>().ToList();
            var command = (RecordingCommand)TabReorderBehavior.GetMoveCommand(harness.TabControl)!;

            RaiseDrop(harness.TabControl, tabs[1], 1, TitleOf(tabs[1]));

            Assert.Empty(command.Executed);
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Drop_OfTabFromAnotherTabControl_IsIgnored()
    {
        var target = ShowTabControl();
        var other = ShowTabControl();
        try
        {
            var targetTabs = target.TabControl.GetVisualDescendants().OfType<TabItem>().ToList();
            var foreignTab = other.TabControl.GetVisualDescendants().OfType<TabItem>().First();
            var command = (RecordingCommand)TabReorderBehavior.GetMoveCommand(target.TabControl)!;

            RaiseDrop(target.TabControl, foreignTab, 0, TitleOf(targetTabs[2]));

            Assert.Empty(command.Executed);
        }
        finally
        {
            target.Window.Close();
            other.Window.Close();
        }
    }


    private static ItemsPresenter StripPresenter(TabControl tabControl) =>
        tabControl.GetVisualDescendants().OfType<ItemsPresenter>().First(p => p.Name == "PART_ItemsPresenter");

    private static Rect StripBounds(TabControl tabControl)
    {
        var presenter = StripPresenter(tabControl);
        var origin = presenter.TranslatePoint(new Point(0, 0), tabControl)!.Value;
        return new Rect(origin, presenter.Bounds.Size);
    }

    private static RecordingCommand Reorder(TabControl tabControl) => (RecordingCommand)TabReorderBehavior.GetReorderCommand(tabControl);

    [AvaloniaFact]
    public void Drop_OverContentArea_IsIgnoredByBothCommands()
    {
        var harness = ShowTabControl();
        try
        {
            var tabs = harness.TabControl.GetVisualDescendants().OfType<TabItem>().ToList();
            var strip = StripBounds(harness.TabControl);
            var overContent = new Point(strip.X + 10, strip.Bottom + 20);

            RaiseDrop(harness.TabControl, tabs[0], 0, harness.TabControl, overContent);

            Assert.Empty(((RecordingCommand)TabReorderBehavior.GetMoveCommand(harness.TabControl)!).Executed);
            Assert.Empty(Reorder(harness.TabControl).Executed);
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Drop_OnEmptyStripRightOfTabs_MovesToLastIndexForBothCommands()
    {
        var harness = ShowTabControl();
        try
        {
            var tabs = harness.TabControl.GetVisualDescendants().OfType<TabItem>().ToList();
            var strip = StripBounds(harness.TabControl);
            var emptyStrip = new Point(harness.TabControl.Bounds.Width - 2, strip.Y + strip.Height / 2);

            RaiseDrop(harness.TabControl, tabs[0], 0, harness.TabControl, emptyStrip);

            Assert.Equal(new TabMoveRequest(0, 2), Assert.IsType<TabMoveRequest>(Assert.Single(((RecordingCommand)TabReorderBehavior.GetMoveCommand(harness.TabControl)!).Executed)));
            Assert.Equal("Unknown:0:2", Assert.Single(Reorder(harness.TabControl).Executed));
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(0.0, true)]   // top edge of the band (inclusive)
    [InlineData(1.0, true)]   // inside
    [InlineData(-1.0, false)] // 1 px above the band (relative to its top edge)
    public void IsInTabStripBand_TopPlacement_UsesInclusiveVerticalBand(double yOffsetFromTop, bool expected)
    {
        var harness = ShowTabControl();
        try
        {
            var strip = StripBounds(harness.TabControl);

            Assert.Equal(expected, TabReorderBehavior.IsInTabStripBand(harness.TabControl, new Point(strip.X + 5, strip.Y + yOffsetFromTop)));
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaFact]
    public void IsInTabStripBand_TopPlacement_BottomEdgeInclusiveAndBelowExcluded()
    {
        var harness = ShowTabControl();
        try
        {
            var strip = StripBounds(harness.TabControl);

            Assert.True(TabReorderBehavior.IsInTabStripBand(harness.TabControl, new Point(strip.X + 5, strip.Bottom)));
            Assert.False(TabReorderBehavior.IsInTabStripBand(harness.TabControl, new Point(strip.X + 5, strip.Bottom + 1)));
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(Dock.Left)]
    [InlineData(Dock.Right)]
    public void IsInTabStripBand_VerticalPlacement_UsesHorizontalBandOverFullHeight(Dock placement)
    {
        var harness = ShowTabControl(placement: placement);
        try
        {
            var strip = StripBounds(harness.TabControl);
            var overContentX = placement == Dock.Left ? strip.Right + 20 : strip.X - 20;

            Assert.True(TabReorderBehavior.IsInTabStripBand(harness.TabControl, new Point(strip.X + strip.Width / 2, harness.TabControl.Bounds.Height - 1)));
            Assert.False(TabReorderBehavior.IsInTabStripBand(harness.TabControl, new Point(overContentX, strip.Y + 5)));
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaFact]
    public void IsInTabStripBand_TemplateWithoutItemsPresenterPart_KeepsMoveToEndBehavior()
    {
        var notTemplated = new TabControl();

        Assert.True(TabReorderBehavior.IsInTabStripBand(notTemplated, new Point(500, 500)));
    }

    [AvaloniaFact]
    public void IsInTabStripBand_AfterTheTemplateIsReplaced_FollowsTheNewStripPresenter()
    {
        var harness = ShowTabControl();
        try
        {
            var oldStrip = StripBounds(harness.TabControl);
            Assert.True(TabReorderBehavior.IsInTabStripBand(harness.TabControl, new Point(oldStrip.X + 5, oldStrip.Y + 1)));

            // Same parts, strip moved to the bottom edge.
            harness.TabControl.Template = new FuncControlTemplate<TabControl>((_, scope) =>
            {
                var strip = new ItemsPresenter { Name = "PART_ItemsPresenter" };
                strip[!ItemsPresenter.ItemsPanelProperty] = new TemplateBinding(ItemsControl.ItemsPanelProperty);
                scope.Register("PART_ItemsPresenter", strip);
                DockPanel.SetDock(strip, Dock.Bottom);

                var content = new ContentPresenter { Name = "PART_SelectedContentHost" };
                content[!ContentPresenter.ContentProperty] = new TemplateBinding(TabControl.SelectedContentProperty);
                scope.Register("PART_SelectedContentHost", content);

                var root = new DockPanel();
                root.Children.Add(strip);
                root.Children.Add(content);
                return root;
            });
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            var newStrip = StripBounds(harness.TabControl);
            Assert.True(newStrip.Y > oldStrip.Y, "the replaced template must place the strip lower");
            Assert.True(TabReorderBehavior.IsInTabStripBand(harness.TabControl, new Point(newStrip.X + 5, newStrip.Y + 1)));
            Assert.False(TabReorderBehavior.IsInTabStripBand(harness.TabControl, new Point(oldStrip.X + 5, oldStrip.Y + 1)));
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Drop_OnTab_StillUsesThatTabsIndexRegardlessOfBand()
    {
        var harness = ShowTabControl();
        try
        {
            var tabs = harness.TabControl.GetVisualDescendants().OfType<TabItem>().ToList();
            var strip = StripBounds(harness.TabControl);

            RaiseDrop(harness.TabControl, tabs[0], 0, TitleOf(tabs[1]), new Point(strip.X + 5, strip.Bottom + 50));

            Assert.Equal(new TabMoveRequest(0, 1), Assert.IsType<TabMoveRequest>(Assert.Single(((RecordingCommand)TabReorderBehavior.GetMoveCommand(harness.TabControl)!).Executed)));
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaFact]
    public void Click_OnTabCloseButton_StillInvokesButtonCommandWhenBehaviorIsEnabled()
    {
        var harness = ShowTabControl();
        try
        {
            var secondTab = harness.TabControl.GetVisualDescendants().OfType<TabItem>().ElementAt(1);
            var button = secondTab.GetVisualDescendants().OfType<Button>().First();
            var point = CenterOf(button, harness.Window);

            ClickAt(harness.Window, point);

            Assert.Equal(1, harness.CloseClicks);
        }
        finally
        {
            harness.Window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Click_OnTabText_SelectsTab(bool behaviorEnabled)
    {
        var harness = ShowTabControl(behaviorEnabled);
        try
        {
            var thirdTab = harness.TabControl.GetVisualDescendants().OfType<TabItem>().ElementAt(2);
            var point = CenterOf(thirdTab, harness.Window);

            ClickAt(harness.Window, point);

            Assert.Equal(2, harness.TabControl.SelectedIndex);
        }
        finally
        {
            harness.Window.Close();
        }
    }
}
