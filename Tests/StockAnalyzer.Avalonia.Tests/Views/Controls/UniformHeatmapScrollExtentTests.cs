using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Avalonia.Views.Controls;
using StockAnalyzer.Core.Models.Analysis;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

/// <summary>
/// Headless probe and regression guard for MP-1:
/// Heatmap bottom row clipped when tile count exceeds the viewport.
/// Proves hypothesis H1/H2 (ScrollViewer.Padding is not added to Extent.Height, causing the last row
/// to sit under the viewport bottom) and verifies that wrapping UniformHeatmapControl in a Border
/// with Padding="8,8,16,16" and setting ScrollViewer Padding="0" eliminates clipping.
/// </summary>
public sealed class UniformHeatmapScrollExtentTests
{
    private static IReadOnlyList<UniformHeatmapItem> CreateItems(int count)
    {
        var list = new List<UniformHeatmapItem>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new UniformHeatmapItem($"TICK{i:D4}", 0.01 * ((i % 10) - 5)));
        }
        return list;
    }

    /// <summary>
    /// Step 0 Probe verification: Confirms hypothesis H1.
    /// When ScrollViewer has direct Padding="8,8,16,8", Avalonia's ScrollViewer does NOT
    /// include the vertical padding in Extent.Height, causing the bottom tiles to be clipped
    /// at max scroll offset.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(300)]
    [InlineData(1000)]
    public void Probe_DirectScrollViewerWithPadding_ConfirmsH1ExtentLacksPadding(int itemCount)
    {
        var items = CreateItems(itemCount);
        var control = new UniformHeatmapControl { Items = items };
        var scrollViewer = new ScrollViewer
        {
            AllowAutoHide = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(8, 8, 16, 8),
            Content = control
        };

        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = scrollViewer
        };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        try
        {
            var pad = scrollViewer.Padding;
            var extent = scrollViewer.Extent;
            var desired = control.DesiredSize;

            // H1 confirmation: Extent.Height equals control height without padTop + padBottom
            bool extentLacksPadding = extent.Height < desired.Height + pad.Top + pad.Bottom;
            Assert.True(extentLacksPadding, "Probe confirmation: ScrollViewer.Padding is omitted from Extent.Height (Hypothesis H1).");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Verifies the structural fix (Border wrapper with Padding="8,8,16,16" and ScrollViewer Padding="0"):
    /// Across N = 5 (no scroll), 300 (normal scroll), and 1000 (deep scroll),
    /// the extent includes top and bottom padding, and the last tile has at least a 16px bottom gutter.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(5)]
    [InlineData(300)]
    [InlineData(1000)]
    public void WrappedInBorder_PassesAllNoClippingCriteria(int itemCount)
    {
        var items = CreateItems(itemCount);
        var control = new UniformHeatmapControl { Items = items };
        var border = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 8, 16, 16),
            Child = control
        };

        var scrollViewer = new ScrollViewer
        {
            AllowAutoHide = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
            Content = border
        };

        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = scrollViewer
        };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        try
        {
            var extent = scrollViewer.Extent;
            var viewport = scrollViewer.Viewport;
            var desired = control.DesiredSize;
            var pad = border.Padding;

            // Pass criterion 1: Extent.Height >= control.DesiredSize.Height + padTop + padBottom
            Assert.True(
                extent.Height >= desired.Height + pad.Top + pad.Bottom - 0.5,
                $"Extent.Height ({extent.Height}) must be >= desired ({desired.Height}) + padTop ({pad.Top}) + padBottom ({pad.Bottom})");

            // Scroll to the bottom
            double maxOffsetY = Math.Max(0, extent.Height - viewport.Height);
            scrollViewer.Offset = new Vector(0, maxOffsetY);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            // Coordinate of the bottom of the last tile relative to the viewport top:
            // Border is placed at Y = -scrollViewer.Offset.Y within ScrollContentPresenter.
            // Inside Border, control is offset by pad.Top.
            double lastTileBottomInViewport = -scrollViewer.Offset.Y + pad.Top + control.Bounds.Height;
            double gutterAtBottom = viewport.Height - lastTileBottomInViewport;

            // Pass criterion 2: At max offset, viewportBottom - lastTileBottomInViewport >= padBottom (tolerance 0.5 px)
            Assert.True(
                gutterAtBottom >= pad.Bottom - 0.5,
                $"At max scroll offset, bottom gutter ({gutterAtBottom}) must be >= pad.Bottom ({pad.Bottom}), lastTileBottomInVP={lastTileBottomInViewport}, viewport={viewport.Height}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Regression test mounting the real ScatterPlotView:
    /// Confirms that ScatterPlotView.axaml wires the heatmap ScrollViewer with Padding="0"
    /// and wraps UniformHeatmapControl inside a Border with Padding="8,8,16,16" and Transparent background.
    /// </summary>
    [AvaloniaFact]
    public void ScatterPlotView_HeatmapScrollViewer_UsesBorderWrapperWithRequiredPadding()
    {
        var view = new ScatterPlotView();
        var window = new Window
        {
            Width = 900,
            Height = 600,
            Content = view
        };

        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        try
        {
            var heatmapControl = view.GetVisualDescendants().OfType<UniformHeatmapControl>().Single();
            var parentBorder = Assert.IsType<Border>(heatmapControl.Parent);
            var scrollViewer = Assert.IsType<ScrollViewer>(parentBorder.Parent);

            // ScrollViewer padding must be 0 (insets belong to the wrapper Border per SA_UI_INTERACTION §28)
            Assert.Equal(new Thickness(0), scrollViewer.Padding);

            // Border wrapper must have Background="Transparent" for pointer/wheel routing
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(parentBorder.Background);
            Assert.Equal(Colors.Transparent, brush.Color);

            // Padding: left 8, top 8, right 16, bottom 16 (SA_UI_SUBWINDOW_STANDARD §2.2 minimum 16px bottom container padding)
            Assert.Equal(new Thickness(8, 8, 16, 16), parentBorder.Padding);

            // AllowAutoHide must remain false per SA_UI_SUBWINDOW_STANDARD §2.3
            Assert.False(scrollViewer.AllowAutoHide);
            Assert.Equal(ScrollBarVisibility.Auto, scrollViewer.VerticalScrollBarVisibility);
            Assert.Equal(ScrollBarVisibility.Disabled, scrollViewer.HorizontalScrollBarVisibility);
        }
        finally
        {
            window.Close();
        }
    }
}
