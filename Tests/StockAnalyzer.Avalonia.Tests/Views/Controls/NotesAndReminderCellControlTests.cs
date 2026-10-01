using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Messaging;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Tests.TestHelpers;
using StockAnalyzer.Avalonia.ViewModels.Watchlist;
using StockAnalyzer.Avalonia.Views.Controls;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Controls;

/// <summary>Hover-popup contract of the Tickers-tab Notes / Reminder cells, exercised through a real, opened
/// popup: the text is never auto-wrapped, the popup has no width limit (Fluent's default ToolTip MaxWidth of
/// 320 px used to cut every long line at the popup edge), the Notes popup shows NotesPopupText (and is hidden when
/// there is nothing to show) and the Reminder popup the full Reminder. Controls are created only inside
/// [AvaloniaFact] tests (a control needs the headless Avalonia UI thread).</summary>
[Collection("TickerNotesDisplayContext State")]
public class NotesAndReminderCellControlTests
{
    // Wider than any popup text used below, so the window itself never limits the popup width.
    private const double HostWindowWidth = 5000;

    // Fixed label instead of the localized one: keeps the test independent of the process-wide LocalizationManager.
    private const string Label = "MORE-LABEL";

    private static WatchlistItemViewModel CreateViewModel() => new(
        "AAPL", "Apple Inc.", "Technology", "Consumer Electronics",
        150.0m, 155.0m, 149.0m, 153.0m, 1000000, 2.0, 3.0m);

    private static FakeNotesSettingsManager UseContext(int maxCharacters, int maxLines)
    {
        TickerNotesDisplayContext.ResetForTesting();
        var settings = new FakeNotesSettingsManager();
        settings.SetReadMoreMaxCharacters(maxCharacters);
        settings.SetReadMoreMaxLines(maxLines);
        TickerNotesDisplayContext.Initialize(settings, new WeakReferenceMessenger(), () => Label);
        return settings;
    }

    /// <summary>Mounts <paramref name="cell"/> with <paramref name="vm"/>, opens its hover popup and returns the
    /// popup's text block together with the ToolTip that hosts it (null when Avalonia did not put the popup on screen,
    /// which is what a hidden popup looks like).</summary>
    private static (TextBlock Text, ToolTip? Host) OpenPopup(UserControl cell, WatchlistItemViewModel vm)
    {
        cell.DataContext = vm;
        var window = new Window { Width = HostWindowWidth, Height = 400, Content = cell };
        window.Show();

        var panel = Assert.IsType<DockPanel>(cell.Content);
        var cellText = panel.Children.OfType<TextBlock>().Single();
        ToolTip.SetIsOpen(cellText, true);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var popupText = Assert.IsType<TextBlock>(Assert.IsType<ToolTip>(ToolTip.GetTip(cellText)).Content);
        var host = popupText.GetVisualAncestors().OfType<ToolTip>().SingleOrDefault();
        return (popupText, host);
    }

    [AvaloniaFact]
    public void NotesPopup_IsNotWrapped_HasNoWidthLimit_AndShowsNotesPopupText()
    {
        UseContext(maxCharacters: 400, maxLines: 5);
        try
        {
            var vm = CreateViewModel();
            vm.Notes = new string('W', 300); // one long line, within both thresholds

            var (text, host) = OpenPopup(new NotesCellControl(), vm);

            Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
            Assert.True(double.IsPositiveInfinity(host!.MaxWidth));
            Assert.Equal(vm.NotesPopupText, text.Text);
            Assert.Equal(new string('W', 300), text.Text);
            // The 320 px default cap would have clipped this line to ~302 px.
            Assert.True(text.Bounds.Width > 320, $"popup line was clipped to {text.Bounds.Width}px");
        }
        finally
        {
            TickerNotesDisplayContext.ResetForTesting();
        }
    }

    [AvaloniaFact]
    public void NotesPopup_WhenEitherThresholdIsExceeded_IsCutWithReadMoreLine()
    {
        UseContext(maxCharacters: 10, maxLines: 2);
        try
        {
            var byCharacters = CreateViewModel();
            byCharacters.Notes = new string('a', 30);
            var byLines = CreateViewModel();
            byLines.Notes = "1\n2\n3\n4";
            var withinBoth = CreateViewModel();
            withinBoth.Notes = "1\n2\nabc";

            Assert.Equal(new string('a', 10) + "\n" + Label, OpenPopup(new NotesCellControl(), byCharacters).Text.Text);
            Assert.Equal("1\n2\n" + Label, OpenPopup(new NotesCellControl(), byLines).Text.Text);
            Assert.Equal("1\n2\nabc", OpenPopup(new NotesCellControl(), withinBoth).Text.Text);
        }
        finally
        {
            TickerNotesDisplayContext.ResetForTesting();
        }
    }

    [AvaloniaFact]
    public void NotesPopup_IsHiddenWhenThereIsNothingToShow_AndVisibleOtherwise()
    {
        UseContext(maxCharacters: 150, maxLines: 5);
        try
        {
            var empty = CreateViewModel();
            var withText = CreateViewModel();
            withText.Notes = "some article";

            var emptyHost = OpenPopup(new NotesCellControl(), empty).Host;
            var textHost = OpenPopup(new NotesCellControl(), withText).Host;

            Assert.Null(emptyHost);
            Assert.NotNull(textHost);
            Assert.True(textHost!.IsVisible);
        }
        finally
        {
            TickerNotesDisplayContext.ResetForTesting();
        }
    }

    [AvaloniaFact]
    public void ReminderPopup_IsNotWrapped_HasNoWidthLimit_AndShowsFullReminder()
    {
        var vm = CreateViewModel();
        var longReminder = new string('W', 400) + "\nsecond line";
        vm.Reminder = longReminder;

        var (text, host) = OpenPopup(new ReminderCellControl(), vm);

        Assert.Equal(TextWrapping.NoWrap, text.TextWrapping);
        Assert.True(double.IsPositiveInfinity(host!.MaxWidth));
        Assert.Equal(longReminder, text.Text);
        Assert.True(text.Bounds.Width > 320, $"popup line was clipped to {text.Bounds.Width}px");
    }
}
