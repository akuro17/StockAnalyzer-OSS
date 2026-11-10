using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

public class TreeDataGridScrollAlignmentTests
{
    private sealed class TestRow
    {
        public string C0 { get; set; } = "Value 0";
        public string C1 { get; set; } = "Value 1";
        public string C2 { get; set; } = "Value 2";
        public string C3 { get; set; } = "Value 3";
        public string C4 { get; set; } = "Value 4";
        public string C5 { get; set; } = "Value 5";
        public string C6 { get; set; } = "Value 6";
        public string C7 { get; set; } = "Value 7";
    }

    [AvaloniaFact]
    public void TreeDataGrid_HorizontalScroll_AtRightmostBoundary_SynchronizesHeaderAndRowOffset()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(StockAnalyzer.Avalonia.Common.TreeDataGridExtensions).TypeHandle);

        var items = new List<TestRow>();
        for (int i = 0; i < 100; i++)
        {
            items.Add(new TestRow());
        }

        var source = new FlatTreeDataGridSource<TestRow>(items);
        source.Columns.Add(new TextColumn<TestRow, string>("Col 0", x => x.C0, new GridLength(150, GridUnitType.Pixel)));
        source.Columns.Add(new TextColumn<TestRow, string>("Col 1", x => x.C1, new GridLength(150, GridUnitType.Pixel)));
        source.Columns.Add(new TextColumn<TestRow, string>("Col 2", x => x.C2, new GridLength(150, GridUnitType.Pixel)));
        source.Columns.Add(new TextColumn<TestRow, string>("Col 3", x => x.C3, new GridLength(150, GridUnitType.Pixel)));
        source.Columns.Add(new TextColumn<TestRow, string>("Col 4", x => x.C4, new GridLength(150, GridUnitType.Pixel)));
        source.Columns.Add(new TextColumn<TestRow, string>("Col 5", x => x.C5, new GridLength(150, GridUnitType.Pixel)));
        source.Columns.Add(new TextColumn<TestRow, string>("Col 6", x => x.C6, new GridLength(150, GridUnitType.Pixel)));
        source.Columns.Add(new TextColumn<TestRow, string>("Col 7", x => x.C7, new GridLength(150, GridUnitType.Pixel)));

        var grid = new TreeDataGrid
        {
            Source = source,
            Width = 400,
            Height = 300
        };
        grid.SetValue(ScrollViewer.AllowAutoHideProperty, false);
        grid.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        grid.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);

        var window = new Window
        {
            Width = 500,
            Height = 400,
            Content = grid
        };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var headerScroll = grid.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault(sv => sv.Name == "PART_HeaderScrollViewer");
            var rowScroll = grid.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault(sv => sv.Name == "PART_ScrollViewer");

            Assert.NotNull(headerScroll);
            Assert.NotNull(rowScroll);

            var vbar = rowScroll.GetVisualDescendants()
                .OfType<ScrollBar>()
                .FirstOrDefault(sb => sb.Name == "PART_VerticalScrollBar" && sb.Orientation == Orientation.Vertical);

            Assert.NotNull(vbar);
            Assert.True(vbar.IsVisible, "Vertical scrollbar should be visible with 100 rows in 300px height.");
            Assert.True(vbar.Bounds.Width > 0, "Vertical scrollbar width should be positive.");

            // Verify header right margin matches vertical scrollbar width
            Assert.Equal(vbar.Bounds.Width, headerScroll.Margin.Right, precision: 3);

            // Scroll to the rightmost boundary
            rowScroll.Offset = new Vector(double.MaxValue, 0);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // Offset.X of header and rows must match precisely (zero clamp discrepancy)
            Assert.True(rowScroll.Offset.X > 0, "Row scroll offset should have scrolled to the right.");
            Assert.Equal(rowScroll.Offset.X, headerScroll.Offset.X, precision: 3);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TreeDataGrid_NoVerticalScrollBar_HeaderMarginRightStaysZero()
    {
        // Single item, no vertical scrollbar needed
        var items = new List<TestRow> { new() };

        var source = new FlatTreeDataGridSource<TestRow>(items);
        source.Columns.Add(new TextColumn<TestRow, string>("Col 0", x => x.C0, new GridLength(150, GridUnitType.Pixel)));
        source.Columns.Add(new TextColumn<TestRow, string>("Col 1", x => x.C1, new GridLength(150, GridUnitType.Pixel)));
        source.Columns.Add(new TextColumn<TestRow, string>("Col 2", x => x.C2, new GridLength(150, GridUnitType.Pixel)));
        source.Columns.Add(new TextColumn<TestRow, string>("Col 3", x => x.C3, new GridLength(150, GridUnitType.Pixel)));

        var grid = new TreeDataGrid
        {
            Source = source,
            Width = 300,
            Height = 300
        };
        grid.SetValue(ScrollViewer.AllowAutoHideProperty, false);

        var window = new Window
        {
            Width = 400,
            Height = 400,
            Content = grid
        };

        try
        {
            window.Show();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var headerScroll = grid.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault(sv => sv.Name == "PART_HeaderScrollViewer");
            var rowScroll = grid.GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault(sv => sv.Name == "PART_ScrollViewer");

            Assert.NotNull(headerScroll);
            Assert.NotNull(rowScroll);

            // Header margin right should be 0 because vertical scrollbar is not active
            Assert.Equal(0.0, headerScroll.Margin.Right, precision: 3);

            // Scroll to the rightmost boundary
            rowScroll.Offset = new Vector(double.MaxValue, 0);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(rowScroll.Offset.X, headerScroll.Offset.X, precision: 3);
        }
        finally
        {
            window.Close();
        }
    }
}
