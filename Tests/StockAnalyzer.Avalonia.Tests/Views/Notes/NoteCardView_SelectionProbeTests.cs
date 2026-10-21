using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Notes;

/// <summary>
/// P0 probe (docs/sa_ai_context_NotesTextSelection section 4, Q1-Q8): characterization tests that pin how Avalonia 11.3.10's
/// <see cref="SelectableTextBlock"/> behaves when its Inlines mix <see cref="Run"/>s with <see cref="InlineUIContainer"/>s - the exact
/// shape NoteCardView.RebuildBody builds for a Note body that contains a clickable hashtag/URL. The probe builds a stand-alone block
/// (no product file is involved), so every assertion is a fact about the framework that the product decision table relies on:
/// the selection offsets count an InlineUIContainer as one position, but <c>SelectedText</c> is cut from <c>Inlines.Text</c>, which
/// counts it as zero - so the native <c>SelectedText</c>/Ctrl+C path drops the button text and skews every offset after it (row F-Map).
/// If an Avalonia upgrade changes any of these, this file fails first and the decision row must be re-evaluated.
/// </summary>
public class NoteCardView_SelectionProbeTests
{
    // Pointer hit testing runs against the composed frame, which lags behind a layout change: let it settle first.
    private static void Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>"ab " + [Button "#tag"] + " cd": the shape of a body with one clickable hashtag. Text positions in the layout:
    /// a=0 b=1 space=2 container=3 space=4 c=5 d=6, end=7.</summary>
    private static (SelectableTextBlock Block, Window Window) MountMixedBlock(Control? sibling = null, bool transparentBackground = false)
    {
        var block = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Focusable = true };
        if (transparentBackground)
        {
            block.Background = Brushes.Transparent;
        }

        block.Inlines!.Add(new Run("ab "));
        block.Inlines.Add(new InlineUIContainer(new Button { Content = new TextBlock { Text = "#tag" } }));
        block.Inlines.Add(new Run(" cd"));

        var panel = new StackPanel { Width = 400 };
        panel.Children.Add(block);
        if (sibling is not null)
        {
            panel.Children.Add(sibling);
        }

        var window = new Window { Content = panel, Width = 400, Height = 200 };
        window.Show();
        Settle();
        return (block, window);
    }

    private static void Drag(Window window, double fromX, double toX, double y)
    {
        window.MouseMove(new Point(fromX, y));
        Settle();
        window.MouseDown(new Point(fromX, y), MouseButton.Left);
        window.MouseMove(new Point(toX, y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(toX, y), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void RightClick(Window window, Point point)
    {
        window.MouseMove(point);
        Settle();
        window.MouseDown(point, MouseButton.Right);
        try
        {
            window.MouseUp(point, MouseButton.Right);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("job loop"))
        {
            // The release has been processed; the headless platform's frozen clock cannot finish the flyout transition.
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Q1: with Run-only inlines SelectedText is exactly the substring.</summary>
    [AvaloniaFact]
    public void Q1_RunOnlyBlock_SelectedTextIsTheSubstring()
    {
        var block = new SelectableTextBlock();
        block.Inlines!.Add(new Run("Hello World"));
        var window = new Window { Content = block, Width = 400, Height = 100 };
        window.Show();
        Settle();
        try
        {
            block.SelectionStart = 2;
            block.SelectionEnd = 7;
            Assert.Equal("llo W", block.SelectedText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Q2: Inlines.Text omits the container, so SelectedText omits the button text and reports "" for any range that ends
    /// beyond the 6-character text.</summary>
    [AvaloniaFact]
    public void Q2_MixedBlock_SelectedTextOmitsTheContainerAndSkewsOffsets()
    {
        var (block, window) = MountMixedBlock();
        try
        {
            Assert.Null(block.Text);
            Assert.Equal("ab  cd", block.Inlines!.Text);

            block.SelectAll();
            Assert.Equal(0, block.SelectionStart);
            Assert.Equal(6, block.SelectionEnd);
            Assert.Equal("ab  cd", block.SelectedText);

            block.SelectionStart = 0;
            block.SelectionEnd = 4;
            Assert.Equal("ab  ", block.SelectedText);

            block.SelectionStart = 4;
            block.SelectionEnd = 5;
            Assert.Equal("c", block.SelectedText);

            block.SelectionStart = 4;
            block.SelectionEnd = 7;
            Assert.Equal(string.Empty, block.SelectedText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Q3: in layout (hit-test / selection) coordinates the container occupies exactly one position: the text end is 7, not 6.</summary>
    [AvaloniaFact]
    public void Q3_ContainerOccupiesOneLayoutPosition()
    {
        var (block, window) = MountMixedBlock();
        try
        {
            var layout = block.TextLayout;
            Assert.Equal(3, layout.HitTestPoint(new Point(31, 10)).TextPosition);
            Assert.Equal(5, layout.HitTestPoint(new Point(100, 10)).TextPosition);
            Assert.Equal(7, layout.HitTestPoint(new Point(300, 10)).TextPosition);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Q3 (real drag): a drag that ends past the last character selects layout [0,7), and SelectedText is "" - the native
    /// Ctrl+C would copy nothing. Also pins that a block WITHOUT a Background is not hit-testable at all (selection never starts),
    /// which is why the product body block must declare <c>Background="Transparent"</c>.</summary>
    [AvaloniaTheory]
    [InlineData(false, 10.0, 0, 0)]
    [InlineData(true, 1.0, 0, 7)]
    [InlineData(true, 10.0, 1, 7)]
    public void Q3_RealMouseDrag_ReportsLayoutOffsetsAndEmptySelectedText(bool transparentBackground, double downX, int expectedStart, int expectedEnd)
    {
        var (block, window) = MountMixedBlock(null, transparentBackground);
        try
        {
            Drag(window, downX, 200, 13);
            Assert.Equal(expectedStart, block.SelectionStart);
            Assert.Equal(expectedEnd, block.SelectionEnd);
            if (expectedEnd == 7)
            {
                Assert.Equal(string.Empty, block.SelectedText);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Q4: a fresh block already carries a built-in MenuFlyout as ContextFlyout (so the product replaces it explicitly).</summary>
    [AvaloniaFact]
    public void Q4_FreshBlock_HasABuiltInContextFlyout()
    {
        var (block, window) = MountMixedBlock();
        try
        {
            Assert.IsType<MenuFlyout>(block.ContextFlyout);
            Assert.Null(block.ContextMenu);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Q5: a plain focus loss clears the selection; with a ContextFlyout open the selection survives a focus move.</summary>
    [AvaloniaFact]
    public void Q5_FocusLoss_ClearsSelectionUnlessAFlyoutIsOpen()
    {
        var other = new TextBox { Text = "x", Focusable = true };
        var (block, window) = MountMixedBlock(other);
        try
        {
            block.Focus();
            Dispatcher.UIThread.RunJobs();
            block.SelectionStart = 0;
            block.SelectionEnd = 2;
            other.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, block.SelectionStart);
            Assert.Equal(0, block.SelectionEnd);

            var flyout = new MenuFlyout { ItemsSource = new[] { new MenuItem { Header = "x" } } };
            block.ContextFlyout = flyout;
            block.Focus();
            Dispatcher.UIThread.RunJobs();
            block.SelectionStart = 0;
            block.SelectionEnd = 2;
            flyout.ShowAt(block);
            Dispatcher.UIThread.RunJobs();
            other.Focus();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, block.SelectionEnd);
            Assert.Equal("ab", block.SelectedText);
            flyout.Hide();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Q6: a right-click on an inline Button bubbles ContextRequested to the block, so the block's ContextFlyout opens there too.</summary>
    [AvaloniaTheory]
    [InlineData(10.0, 13.0)]
    [InlineData(59.0, 13.0)]
    public void Q6_RightClick_OpensTheBlocksContextFlyout_AlsoOverAnInlineButton(double x, double y)
    {
        var (block, window) = MountMixedBlock(null, transparentBackground: true);
        try
        {
            var flyout = new MenuFlyout { ItemsSource = new[] { new MenuItem { Header = "x" } } };
            block.ContextFlyout = flyout;
            var opening = 0;
            flyout.Opening += (_, _) => opening++;

            RightClick(window, new Point(x, y));

            Assert.Equal(1, opening);
            try { flyout.Hide(); } catch (Exception) { /* headless flyout transition */ }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Q5/Q6: the selection made by a drag is still intact when the flyout starts opening after a right-click (inside or
    /// outside the selected range) - so the product snapshot taken in <c>Opening</c> sees the real selection.</summary>
    [AvaloniaTheory]
    [InlineData(10.0)]
    [InlineData(75.0)]
    public void Q6_RightClickAfterDragSelection_KeepsTheSelectionAtFlyoutOpening(double rightClickX)
    {
        var (block, window) = MountMixedBlock(null, transparentBackground: true);
        try
        {
            var flyout = new MenuFlyout { ItemsSource = new[] { new MenuItem { Header = "x" } } };
            block.ContextFlyout = flyout;
            (int Start, int End)? atOpening = null;
            flyout.Opening += (_, _) => atOpening = (block.SelectionStart, block.SelectionEnd);

            Drag(window, 1, 100, 10);
            Assert.Equal(0, block.SelectionStart);
            Assert.Equal(5, block.SelectionEnd);

            RightClick(window, new Point(rightClickX, 10));

            Assert.Equal((0, 5), atOpening);
            try { flyout.Hide(); } catch (Exception) { /* headless flyout transition */ }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Q7/Q8: the headless clipboard round-trips text, and the native Ctrl+C writes SelectedText - which for a range crossing
    /// the container is the skewed text of Q2, not what is drawn.</summary>
    [AvaloniaFact]
    public async Task Q7_Q8_Clipboard_RoundTripsAndNativeCtrlCWritesSelectedText()
    {
        var (block, window) = MountMixedBlock();
        try
        {
            var clipboard = TopLevel.GetTopLevel(window)!.Clipboard;
            Assert.NotNull(clipboard);

            await clipboard!.SetTextAsync("sentinel");
            Assert.Equal("sentinel", await clipboard.TryGetTextAsync());

            block.Focus();
            Dispatcher.UIThread.RunJobs();
            block.SelectionStart = 0;
            block.SelectionEnd = 2;
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("ab", await clipboard.TryGetTextAsync());

            await clipboard.SetTextAsync("sentinel");
            block.SelectAll();
            window.KeyPressQwerty(PhysicalKey.C, RawInputModifiers.Control);
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("ab  cd", await clipboard.TryGetTextAsync());
        }
        finally
        {
            window.Close();
        }
    }
}
