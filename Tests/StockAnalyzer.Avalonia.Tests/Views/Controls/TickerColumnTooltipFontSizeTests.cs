using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Avalonia.Views.Controls;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

/// <summary>
/// Regression: the Ticker list's Notes and Tags column hover popups must follow
/// Settings &gt; Fonts &gt; Tooltip Font Size (TooltipFontSize), like every other column popup
/// (SA_UI_INTERACTION.md, Ticker List Column Hover Tooltip). Notes used a bare string tip (inherited
/// BaseFontSize) and Tags chips were bound to HelperFontSize.
/// </summary>
[Collection("DrawingThemeContext State")]
public class TickerColumnTooltipFontSizeTests
{
    private const double DistinctTooltipSize = 23.0;
    private const double DistinctHelperSize = 17.0;

    private static void WithDistinctFontResources(Action assertions)
    {
        var resources = Application.Current!.Resources;
        var previousTooltip = resources["TooltipFontSize"];
        var previousHelper = resources["HelperFontSize"];
        try
        {
            resources["TooltipFontSize"] = DistinctTooltipSize;
            resources["HelperFontSize"] = DistinctHelperSize;
            assertions();
        }
        finally
        {
            resources["TooltipFontSize"] = previousTooltip ?? FontDefaults.Tooltip;
            resources["HelperFontSize"] = previousHelper ?? FontDefaults.Helper;
        }
    }

    // Resource bindings resolve through the logical tree, so the tip content must be attached to a
    // window (as the real popup content is) before its FontSize can be observed. The window is always
    // closed: a leftover open window makes later headless input tests (wheel/hit-testing) flaky.
    private static void WhileAttached(Control control, Action assertions)
    {
        var window = new Window { Content = control };
        window.Show();
        try
        {
            assertions();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void NotesColumnTooltip_UsesTooltipFontSize()
    {
        WithDistinctFontResources(() =>
        {
            var cell = new NotesCellControl();
            var container = Assert.IsType<DockPanel>(cell.Content);
            var textBlock = Assert.Single(container.Children, c => c is TextBlock);

            var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(textBlock));

            WhileAttached(tip, () => Assert.Equal(DistinctTooltipSize, tip.FontSize));
        });
    }

    [AvaloniaFact]
    public void TagsColumnTooltipChips_UseTooltipFontSize()
    {
        WithDistinctFontResources(() =>
        {
            var cell = new TagCellControl();
            var container = Assert.IsType<DockPanel>(cell.Content);
            var tooltipPanel = Assert.IsType<ItemsControl>(ToolTip.GetTip(container));

            var chip = Assert.IsType<Border>(tooltipPanel.ItemTemplate!.Build("growth"));
            var chipText = Assert.IsType<TextBlock>(chip.Child);

            WhileAttached(chip, () => Assert.Equal(DistinctTooltipSize, chipText.FontSize));
        });
    }
}
