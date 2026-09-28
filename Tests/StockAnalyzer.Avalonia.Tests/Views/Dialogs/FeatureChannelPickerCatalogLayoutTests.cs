using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views.Dialogs;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Dialogs;

/// <summary>
/// Locks the middle "catalog" column of FeatureChannelPickerView to a fixed width so a long indicator
/// name can never widen it. The column was originally <c>280*</c>; on TrainingWizardWindow's measure
/// path (TabControl > StackPanel > body ScrollViewer) a star column was observed with real fonts to
/// grow to fit the longest <c>DisplayName</c>, so the column is now a fixed px width
/// (see <c>FeatureChannelPickerView.axaml</c> root Grid comment and SA_UI_INTERACTION.md section 26).
/// Every catalog row line also trims with <c>CharacterEllipsis</c>.
/// </summary>
public class FeatureChannelPickerCatalogLayoutTests
{
    // Must equal the middle track in FeatureChannelPickerView.axaml's
    // <Grid ColumnDefinitions="220*,Auto,320,Auto,450*">.
    private const double FixedCatalogColumnWidth = 320;

    private static (Window window, FeatureChannelPickerView view) Mount(double width)
    {
        var view = new FeatureChannelPickerView
        {
            DataContext = new FeatureChannelPickerViewModel(new IndicatorFactory())
        };
        var window = new Window { Content = view, Width = width, Height = 720 };
        window.Show();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (window, view);
    }

    [AvaloniaTheory]
    [InlineData(1180)]
    [InlineData(1000)]
    [InlineData(860)]
    public void CatalogColumn_HasFixedWidth_AndEveryRowLineEllipsises(double width)
    {
        var (window, view) = Mount(width);
        try
        {
            var pickerGrid = view.GetVisualDescendants().OfType<Grid>()
                .Single(g => g.ColumnDefinitions.Count == 5);

            double catalogW = pickerGrid.ColumnDefinitions[2].ActualWidth;

            // The whole point: the catalog column is the SAME width at every window size and for every
            // catalog content - it does not track the longest indicator name and does not scale.
            Assert.Equal(FixedCatalogColumnWidth, catalogW, precision: 0);

            // Two ListBoxes carry Classes="catalogList" (the indicator catalog and the O/H/L/C price
            // list); the indicator catalog is the visible, populated one on the default nav mode.
            var catalog = view.GetVisualDescendants().OfType<ListBox>()
                .Where(lb => lb.Classes.Contains("catalogList"))
                .OrderByDescending(lb => lb.ItemCount)
                .First();

            var lines = catalog.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => !string.IsNullOrEmpty(t.Text))
                .ToList();
            Assert.NotEmpty(lines);
            Assert.All(lines, t => Assert.Equal(TextTrimming.CharacterEllipsis, t.TextTrimming));

            // No realized row line may exceed the fixed catalog column bounds.
            foreach (var t in lines)
            {
                Assert.True(t.Bounds.Width <= catalogW + 1,
                    $"row line '{t.Text}' width {t.Bounds.Width:F1} exceeds catalog column {catalogW:F1}");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
