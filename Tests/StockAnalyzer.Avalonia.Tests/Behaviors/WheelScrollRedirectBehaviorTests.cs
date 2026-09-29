using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Behaviors;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Behaviors;

/// <summary>
/// Unit coverage for <see cref="WheelScrollRedirectBehavior"/> paired with the shared
/// <c>SidebarScrollViewerTheme</c> -- the exact combination that DrawingSettingsDialog,
/// FeatureChannelPickerView and TrainingWizardWindow rely on for "the wheel always scrolls the
/// settings form". Exercises the reusable unit directly so each consuming dialog does not need its
/// own heavyweight view/VM wiring test.
/// </summary>
public class WheelScrollRedirectBehaviorTests
{
    private const double SmallContentSize = 40;
    private const double SmallContentWindowSize = 200;
    private const double WheelDeltaY = -3;
    private const int BlankSpaceRowCount = 24;
    private const double BlankSpaceRowHeight = 28;
    private const double BlankSpaceRowGap = 12;
    private const double BlankSpaceWindowWidth = 320;
    private const double BlankSpaceWindowHeight = 300;
    private const double BlankSpaceProbeInsetX = 60;
    private const double BlankSpaceProbeY = 20;

    private static (Window window, ScrollViewer scrollViewer, StackPanel content, ComboBox combo, NumericUpDown spin) BuildHost()
    {
        var content = new StackPanel { Spacing = 12 };
        for (int i = 0; i < 24; i++)
        {
            content.Children.Add(new Border { Height = 28, Child = new TextBlock { Text = $"row {i}" } });
        }

        var spin = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 10 };
        content.Children.Insert(3, spin);

        var combo = new ComboBox { ItemsSource = new[] { "alpha", "beta", "gamma" }, SelectedIndex = 0 };
        content.Children.Insert(6, combo);

        var scrollViewer = new ScrollViewer
        {
            Theme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content
        };
        WheelScrollRedirectBehavior.SetIsEnabled(scrollViewer, true);

        var window = new Window { Content = scrollViewer, Width = 320, Height = 300 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (window, scrollViewer, content, combo, spin);
    }

    [AvaloniaFact]
    public void SidebarScrollViewerTheme_TemplateGridPaintsBackground_SoScrollViewerIsHitTestable()
    {
        var scrollViewer = new ScrollViewer
        {
            Theme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!,
            Content = new Border { Width = 40, Height = 40 }
        };
        var window = new Window { Content = scrollViewer, Width = 200, Height = 200 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // The template root Grid must bind Background (to the theme's Transparent setter) so the
            // ScrollViewer is hit-testable over blank areas, exactly like the default Avalonia
            // template. Without this a wheel over the gaps between controls routes nowhere.
            var templateGrid = scrollViewer.GetVisualDescendants().OfType<Grid>().First();
            Assert.NotNull(templateGrid.Background);
            var solid = Assert.IsAssignableFrom<ISolidColorBrush>(templateGrid.Background);
            Assert.Equal(Colors.Transparent, solid.Color);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>sa_minimal_fix (Notes timeline wheel over reply connector gap): the theme's
    /// ScrollContentPresenter - the control that handles the wheel - must itself be hit-testable over
    /// blank content, not only the template Grid, or a wheel over blank space never reaches it.</summary>
    [AvaloniaFact]
    public void SidebarScrollViewerTheme_ContentPresenterPaintsBackground_SoBlankContentSpaceIsHitTestable()
    {
        var scrollViewer = new ScrollViewer
        {
            Theme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!,
            Content = new Border { Width = SmallContentSize, Height = SmallContentSize }
        };
        var window = new Window { Content = scrollViewer, Width = SmallContentWindowSize, Height = SmallContentWindowSize };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var presenter = scrollViewer.GetVisualDescendants().OfType<ScrollContentPresenter>().Single();
            var solid = Assert.IsAssignableFrom<ISolidColorBrush>(presenter.Background);
            Assert.Equal(Colors.Transparent, solid.Color);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Same theme, WITHOUT WheelScrollRedirectBehavior (as in the Notes timeline): a wheel over
    /// blank space between content controls must still scroll the ScrollViewer.</summary>
    [AvaloniaFact]
    public void SidebarScrollViewerTheme_WheelOverBlankContentSpace_ScrollsWithoutRedirectBehavior()
    {
        var content = new StackPanel();
        for (int i = 0; i < BlankSpaceRowCount; i++)
        {
            content.Children.Add(new Border { Width = SmallContentSize, Height = BlankSpaceRowHeight, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, BlankSpaceRowGap) });
        }

        var scrollViewer = new ScrollViewer
        {
            Theme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content
        };
        var window = new Window { Content = scrollViewer, Width = BlankSpaceWindowWidth, Height = BlankSpaceWindowHeight };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height, $"extent={scrollViewer.Extent} viewport={scrollViewer.Viewport}");

            // Right of the narrow, left-aligned Borders: blank content space with no control under it.
            var blankPoint = new Point(window.Width - BlankSpaceProbeInsetX, BlankSpaceProbeY);
            Assert.IsNotType<Border>(window.GetVisualAt(blankPoint));

            // Establish the pointer position and let the input pipeline process it before the wheel;
            // a wheel sent with no prior settled pointer-over is dropped by the headless platform.
            window.MouseMove(blankPoint);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.MouseWheel(blankPoint, new Vector(0, WheelDeltaY));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.True(scrollViewer.Offset.Y > 0, $"offset={scrollViewer.Offset} hit={window.GetVisualAt(blankPoint)?.GetType().Name}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WheelOverBlankGap_ScrollsTheForm()
    {
        var (window, scrollViewer, content, _, _) = BuildHost();
        try
        {
            Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);

            var firstRow = content.Children.OfType<Control>().First(c => c.Bounds.Height > 0);
            var gapPoint = content.TranslatePoint(
                new Point(firstRow.Bounds.X + 8, firstRow.Bounds.Bottom + 6), window) ?? default;

            var before = scrollViewer.Offset.Y;
            window.MouseWheel(gapPoint, new Vector(0, -3));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.True(scrollViewer.Offset.Y > before);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WheelOverFocusedComboBox_ScrollsTheForm_WithoutChangingSelection()
    {
        var (window, scrollViewer, _, combo, _) = BuildHost();
        try
        {
            combo.Focus();
            Dispatcher.UIThread.RunJobs();

            var selectionBefore = combo.SelectedIndex;
            var offsetBefore = scrollViewer.Offset.Y;

            var point = combo.TranslatePoint(
                new Point(combo.Bounds.Width / 2, combo.Bounds.Height / 2), window) ?? default;
            window.MouseWheel(point, new Vector(0, -3));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.True(scrollViewer.Offset.Y > offsetBefore);
            Assert.Equal(selectionBefore, combo.SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WheelOverNestedScrollableListBox_DoesNotHijackTheOuterScroll()
    {
        // Regression: a settings ScrollViewer that carries this behavior may still host a genuinely
        // scrollable inner control (e.g. FeatureChannelPicker's catalog ListBox nested inside the
        // Training Wizard body). The wheel over that inner ListBox must scroll the ListBox, not be
        // stolen by the outer ScrollViewer.
        var listBox = new ListBox
        {
            Height = 90,
            ItemsSource = Enumerable.Range(0, 60).Select(i => $"item {i}").ToArray()
        };

        var outerContent = new StackPanel { Spacing = 12 };
        outerContent.Children.Add(new Border { Height = 40, Child = new TextBlock { Text = "header" } });
        outerContent.Children.Add(listBox);
        for (int i = 0; i < 20; i++)
        {
            outerContent.Children.Add(new Border { Height = 28, Child = new TextBlock { Text = $"tail {i}" } });
        }

        var outer = new ScrollViewer
        {
            Theme = (ControlTheme)Application.Current!.FindResource("SidebarScrollViewerTheme")!,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = outerContent
        };
        WheelScrollRedirectBehavior.SetIsEnabled(outer, true);

        var window = new Window { Content = outer, Width = 320, Height = 260 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var innerScroller = listBox.GetVisualDescendants().OfType<ScrollViewer>().First();
            Assert.True(innerScroller.Extent.Height > innerScroller.Viewport.Height,
                "test setup: the inner ListBox must overflow so it is itself scrollable");

            var outerBefore = outer.Offset.Y;
            var innerBefore = innerScroller.Offset.Y;

            var point = listBox.TranslatePoint(
                new Point(listBox.Bounds.Width / 2, listBox.Bounds.Height / 2), window) ?? default;
            window.MouseWheel(point, new Vector(0, -3));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(outerBefore, outer.Offset.Y); // outer form must not have moved
            Assert.True(innerScroller.Offset.Y > innerBefore); // the ListBox scrolled instead
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void WheelOverFocusedNumericUpDown_ScrollsTheForm_WithoutChangingValue()
    {
        var (window, scrollViewer, _, _, spin) = BuildHost();
        try
        {
            spin.Focus();
            Dispatcher.UIThread.RunJobs();

            var valueBefore = spin.Value;
            var offsetBefore = scrollViewer.Offset.Y;

            var point = spin.TranslatePoint(
                new Point(spin.Bounds.Width / 2, spin.Bounds.Height / 2), window) ?? default;
            window.MouseWheel(point, new Vector(0, -3));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.True(scrollViewer.Offset.Y > offsetBefore);
            Assert.Equal(valueBefore, spin.Value);
        }
        finally
        {
            window.Close();
        }
    }
}
