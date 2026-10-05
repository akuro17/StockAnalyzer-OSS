using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Watchlist;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// Verifies ColumnChooserWindow complies with SA_UI_SUBWINDOW_STANDARD.md:
/// 1. ExtendClientAreaToDecorationsHint="True", CanResize="False".
/// 2. Scrollable containers have AllowAutoHide="False", VerticalScrollBarVisibility="Auto",
///    HorizontalScrollBarVisibility="Disabled", and ListScrollGutter padding.
/// 3. Footer buttons right-aligned in [OK] [Cancel] [Apply] order with IsDefault="True" and IsCancel="True".
/// 4. No hardcoded font sizes in XAML.
/// </summary>
public class ColumnChooserWindowTests
{
    private static readonly List<WatchlistColumnMetadata> TestColumns = new()
    {
        new WatchlistColumnMetadata("Col_Symbol", "Symbol"),
        new WatchlistColumnMetadata("Col_Name", "Name"),
        new WatchlistColumnMetadata("Col_ReturnOnEquity", "ReturnOnEquity"),
        new WatchlistColumnMetadata("Col_ReturnOnAssets", "ReturnOnAssets"),
        new WatchlistColumnMetadata("Col_TrailingPE", "TrailingPE")
    };

    private static ColumnChooserWindow ShowWindow(ColumnChooserViewModel vm)
    {
        var window = new ColumnChooserWindow { DataContext = vm };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Window_AdheresToSubWindowDecorationsAndResizeStandard()
    {
        var vm = new ColumnChooserViewModel(TestColumns, new[] { "Symbol", "ReturnOnEquity" });
        var window = ShowWindow(vm);
        try
        {
            Assert.True(window.ExtendClientAreaToDecorationsHint);
            Assert.False(window.CanResize);
            Assert.Equal(840, window.Width);
            Assert.Equal(600, window.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ScrollableContainers_HaveAllowAutoHideFalse_AndNoAutoHiding()
    {
        var vm = new ColumnChooserViewModel(TestColumns, new[] { "Symbol", "ReturnOnEquity" });
        var window = ShowWindow(vm);
        try
        {
            var columnsListBox = window.FindControl<ListBox>("ColumnsListBox");
            Assert.NotNull(columnsListBox);
            Assert.False(ScrollViewer.GetAllowAutoHide(columnsListBox), "ColumnsListBox must have ScrollViewer.AllowAutoHide=False");
            Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(columnsListBox));
            Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(columnsListBox));

            // Categories list box
            var listBoxes = window.GetVisualDescendants().OfType<ListBox>().ToList();
            var categoryListBox = listBoxes.FirstOrDefault(lb => lb != columnsListBox);
            Assert.NotNull(categoryListBox);
            Assert.False(ScrollViewer.GetAllowAutoHide(categoryListBox), "Category ListBox must have ScrollViewer.AllowAutoHide=False");
            Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(categoryListBox));
            Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(categoryListBox));

            // Right detail scrollviewer
            var scrollViewers = window.GetVisualDescendants().OfType<ScrollViewer>().ToList();
            var detailScrollViewer = scrollViewers.FirstOrDefault(sv => sv.Parent is Border border && Grid.GetColumn(border) == 2);
            Assert.NotNull(detailScrollViewer);
            Assert.False(detailScrollViewer.AllowAutoHide, "Detail ScrollViewer must have AllowAutoHide=False");
            Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, detailScrollViewer.VerticalScrollBarVisibility);
            Assert.Equal(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, detailScrollViewer.HorizontalScrollBarVisibility);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void FooterButtons_FollowStandardOrderAndDefaultCancelKeyBindings()
    {
        var vm = new ColumnChooserViewModel(TestColumns, new[] { "Symbol", "ReturnOnEquity" });
        var window = ShowWindow(vm);
        try
        {
            var buttons = window.GetVisualDescendants().OfType<Button>().ToList();

            var okButton = buttons.Single(b => b.IsDefault);
            var cancelButton = buttons.Single(b => b.IsCancel);
            var applyButton = buttons.Single(b => b.Parent == okButton.Parent && !b.IsDefault && !b.IsCancel);

            // Verified they share the same right-aligned footer panel
            Assert.Same(okButton.Parent, cancelButton.Parent);
            Assert.Same(okButton.Parent, applyButton.Parent);

            var panel = (Panel)okButton.Parent!;
            int okIndex = panel.Children.IndexOf(okButton);
            int cancelIndex = panel.Children.IndexOf(cancelButton);
            int applyIndex = panel.Children.IndexOf(applyButton);

            // Mandatory Standard Order: [OK] [Cancel] [Apply]
            Assert.True(okIndex < cancelIndex, "[OK] must be positioned to the left of [Cancel]");
            Assert.True(cancelIndex < applyIndex, "[Cancel] must be positioned to the left of [Apply]");
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void XamlSource_UsesListScrollGutter_AndNoHardcodedFontSizes()
    {
        string xamlPath = Path.Combine(TestSolution.Root, "StockAnalyzer.Avalonia", "Views", "Dialogs", "ColumnChooserWindow.axaml");
        string xaml = File.ReadAllText(xamlPath);

        // Standard requirement: scrollable lists must use shared ListScrollGutter
        Assert.Contains("Padding=\"{DynamicResource ListScrollGutter}\"", xaml);

        // Standard requirement: hardcoded font sizes in XAML view files are prohibited
        Assert.DoesNotContain("FontSize=\"13\"", xaml);
        Assert.DoesNotContain("FontSize=\"12\"", xaml);
        Assert.DoesNotContain("FontSize=\"14\"", xaml);
        Assert.DoesNotContain("FontSize=\"16\"", xaml);

        // Standard requirement: Theme buttons
        Assert.Contains("Theme=\"{DynamicResource PrimaryButtonStyle}\"", xaml);
        Assert.Contains("Theme=\"{DynamicResource SecondaryButtonStyle}\"", xaml);
    }
}
