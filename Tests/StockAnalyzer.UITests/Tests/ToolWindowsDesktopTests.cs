using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.UITests.Infrastructure;
using StockAnalyzer.UITests.PageObjects;
using Xunit.Abstractions;

namespace StockAnalyzer.UITests.Tests;

/// <summary>
/// The operation windows of the Tools menu on the owned desktop (no qualified training data needed): each opens from its menu item as one modeless window,
/// a second request reuses it, and Esc closes it. Tools > RL Playback has nothing to replay in an empty data root, so its replay card (the Show button) is not shown.
/// </summary>
[Collection("UI Tests")]
public sealed class ToolWindowsDesktopTests(ITestOutputHelper output) : UITestBase(output)
{
    private AutomationElement Element(Window window, string id) => WaitForElement(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), errorMessage: $"Missing element {id}");

    private void CloseWithEscape(MainWindowPage page, Window window, string[] titles)
    {
        window.SetForeground();
        window.Focus();
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil(() => page.CountProcessWindows(titles) == 0, 10_000, "The window did not close on Esc");
    }

    [Fact(Timeout = 180_000)]
    public void RlPlayback_OpensFromTheToolsMenuAsOneWindow_TheReplayCardIsHiddenWithoutAgents_AndEscCloses()
    {
        LaunchApplication();
        var page = new MainWindowPage(MainWindow!, this);
        string[] titles = RlStoredPriceDesktopTests.Localized("Settings_RlPlayback");

        var window = page.OpenRlPlaybackWindow(titles);
        Element(window, RlUiConstants.PlaybackRefresh);
        Assert.Null(window.FindFirstDescendant(cf => cf.ByAutomationId(RlUiConstants.PlaybackShow)));   // no agent: the replay card is not shown

        page.OpenRlPlaybackWindow(titles);                  // a second request activates the open window
        Assert.Equal(1, page.CountProcessWindows(titles));

        CloseWithEscape(page, window, titles);
    }

    [Fact(Timeout = 180_000)]
    public void PredictionModels_OpensFromTheToolsMenuAsOneWindow_AndEscCloses()
    {
        LaunchApplication();
        var page = new MainWindowPage(MainWindow!, this);
        string[] titles = RlStoredPriceDesktopTests.Localized("Menu_PredictionModels");

        var window = page.OpenPredictionModelsWindow(titles);
        Element(window, PredictionModelsUiConstants.Close);
        Assert.True(Element(window, PredictionModelsUiConstants.Run).IsEnabled);   // the main chart is there, so a prediction can be requested

        page.OpenPredictionModelsWindow(titles);            // a second request activates the open window
        Assert.Equal(1, page.CountProcessWindows(titles));

        CloseWithEscape(page, window, titles);
    }
}
