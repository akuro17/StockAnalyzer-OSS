using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.ViewModels.Dialogs;
using StockAnalyzer.Avalonia.Views;
using StockAnalyzer.Avalonia.Views.Backtest;
using StockAnalyzer.Avalonia.Views.Dialogs;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views;

/// <summary>
/// The CalendarDatePicker button icon has one definition (<c>Assets/CalendarDatePickerIcon.axaml</c>). The windows that used to carry their own copy
/// must include it, draw the icon at the shared size and no longer define a geometry of their own.
/// </summary>
public sealed class SharedCalendarIconTests
{
    private const string IncludeSource = "avares://StockAnalyzer.Avalonia/Assets/CalendarDatePickerIcon.axaml";
    private static readonly string[] ConsumerFiles =
    {
        "Views/RlTrainingWindow.axaml", "Views/Backtest/BacktestConfigurationView.axaml", "Views/Dialogs/TrainingWizardWindow.axaml", "Views/PortfolioRlWindow.axaml",
        "Views/Notes/NoteTimelineView.axaml",
    };

    private static string Root()
    {
        string? directory = System.AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "StockAnalyzer.Avalonia", "StockAnalyzer.Avalonia.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory is null ? throw new DirectoryNotFoundException("The repository root was not found.") : Path.Combine(directory, "StockAnalyzer.Avalonia");
    }

    [Fact]
    public void EveryConsumerIncludesTheSharedIconAndKeepsNoLocalGeometry()
    {
        foreach (string file in ConsumerFiles)
        {
            string xaml = File.ReadAllText(Path.Combine(Root(), file));
            Assert.Contains(IncludeSource, xaml);
            Assert.DoesNotContain("DateRangeIconData", xaml);
            Assert.DoesNotContain("Button#PART_Button", xaml);
        }
    }

    [Fact]
    public void TheTrainingAndBacktestPickersTakeTheIsoFormatFromTheSharedClasses()
    {
        string shared = File.ReadAllText(Path.Combine(Root(), "Assets/CalendarDatePickerIcon.axaml"));
        Assert.Contains("CalendarDatePicker.isoDate", shared);
        Assert.Contains("CalendarDatePicker.isoDateHint", shared);
        foreach (string file in ConsumerFiles.Where(f => !f.Contains("NoteTimeline")))
        {
            string xaml = File.ReadAllText(Path.Combine(Root(), file));
            Assert.DoesNotContain("CustomDateFormatString", xaml);
            Assert.DoesNotContain("YYYY-MM-DD", xaml);
            Assert.Contains("Classes=\"isoDate", xaml);
        }
    }

    /// <summary>The icon and, for the pickers that carry the <c>isoDate</c> classes, the ISO format; the hint only where <paramref name="expectHint"/>.</summary>
    private static void AssertSharedIcons(Control root, int minimumPickers, bool expectHint)
    {
        var pickers = root.GetVisualDescendants().OfType<CalendarDatePicker>().ToArray();
        Assert.True(pickers.Length >= minimumPickers, $"{pickers.Length} pickers");
        foreach (var picker in pickers)
        {
            Assert.Equal(CalendarDatePickerFormat.Custom, picker.SelectedDateFormat);
            Assert.Equal(IsoDatePickerFormat.Format, picker.CustomDateFormatString);
            Assert.Equal(expectHint ? IsoDatePickerFormat.Hint : null, string.IsNullOrEmpty(picker.Watermark) ? null : picker.Watermark);
            var button = picker.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PART_Button");
            var icon = Assert.Single(button.GetVisualDescendants().OfType<PathIcon>());
            Assert.NotNull(icon.Data);
            Assert.Equal(Geometry.Parse(SharedIconGeometries.DateRange).ToString(), icon.Data!.ToString());      // the single geometry copy
            Assert.Equal((CalendarDatePickerIconLayout.Size, CalendarDatePickerIconLayout.Size), (icon.Width, icon.Height));
        }
    }

    [AvaloniaFact]
    public void TheSingleSymbolRlWindowDrawsTheSharedIcon()
    {
        var window = new RlTrainingWindow();
        try { window.Show(); AssertSharedIcons(window, 2, expectHint: true); }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheBacktestConfigurationViewDrawsTheSharedIcon()
    {
        var view = new BacktestConfigurationView();
        var window = new Window { Content = view, Width = 1000, Height = 800 };
        try { window.Show(); AssertSharedIcons(view, 2, expectHint: false); }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TheTrainingWizardDrawsTheSharedIconAndTheIsoFormat()
    {
        var window = new TrainingWizardWindow { DataContext = new TrainingWizardViewModel() };
        try { window.Show(); AssertSharedIcons(window, 2, expectHint: true); }
        finally { window.Close(); }
    }
}
