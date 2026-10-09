using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.UITests.Infrastructure;
using StockAnalyzer.UITests.PageObjects;
using Xunit.Abstractions;

namespace StockAnalyzer.UITests.Tests;

/// <summary>
/// The new interaction layer of the portfolio dialog on the owned desktop: the Tools menu entry opens it, the limitations and the source list
/// are on screen, Start is disabled until a source and a period make a feasible run, and sources are chosen in the list only (no manual tag input).
/// </summary>
[Collection("UI Tests")]
public sealed class PortfolioRlDesktopTests(ITestOutputHelper output) : UITestBase(output)
{
    private AutomationElement Element(Window window, string id) => WaitForElement(() => window.FindFirstDescendant(cf => cf.ByAutomationId(id)), errorMessage: $"Missing portfolio RL element {id}");

    [Fact(Timeout = 180_000)]
    public void MenuOpensTheDialogWithTheListAndStartDisabled()
    {
        LaunchApplication();
        Window window = new MainWindowPage(MainWindow!, this).OpenPortfolioRlWindow();
        foreach (string id in new[] { PrlUiConstants.Sources, PrlUiConstants.Start, PrlUiConstants.PreflightSummary })
            Element(window, id);
        Assert.False(Element(window, PrlUiConstants.Start).IsEnabled);
        Assert.Null(window.FindFirstDescendant(cf => cf.ByAutomationId("Prl_TagInput")));          // sources are chosen in the list only
        window.Close();
    }

    /// <summary>
    /// The stored-model interaction layer: the run kinds are on screen, choosing a stored-model kind shows the base-run selection, the freeze option
    /// (fine-tune only) and the admission line, and Start stays closed while no base run is admitted; going back to training from scratch hides them again.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public void ChoosingAStoredModelKindShowsTheBaseSelectionAndKeepsStartClosedUntilABaseIsAdmitted()
    {
        LaunchApplication();
        Window window = new MainWindowPage(MainWindow!, this).OpenPortfolioRlWindow();
        foreach (string id in new[] { PrlUiConstants.RunKindScratch, PrlUiConstants.RunKindFineTune, PrlUiConstants.RunKindEvaluateBase })
            Element(window, id);
        bool Present(string id) => window.FindFirstDescendant(cf => cf.ByAutomationId(id)) is not null;
        bool Gone(string id) => SpinWait.SpinUntil(() => !Present(id), TimeSpan.FromSeconds(5));
        Assert.False(Present(PrlUiConstants.BaseRun));                               // training from scratch needs no base

        // The card lies below the visible part of the scrolled form, so the radio buttons are chosen through the selection pattern, not a mouse click.
        // The card lies below the visible part of the scrolled form: a mouse click misses it, so the radio buttons are chosen through the selection pattern.
        Element(window, PrlUiConstants.RunKindFineTune).AsRadioButton().IsChecked = true;
        foreach (string id in new[] { PrlUiConstants.BaseRun, PrlUiConstants.FreezeEncoder, PrlUiConstants.Admission })
            Element(window, id);
        Assert.False(Element(window, PrlUiConstants.Start).IsEnabled);

        Element(window, PrlUiConstants.RunKindEvaluateBase).AsRadioButton().IsChecked = true;
        Assert.True(Gone(PrlUiConstants.FreezeEncoder));                             // an evaluation has no freeze
        Assert.True(Present(PrlUiConstants.BaseRun));
        Assert.False(Element(window, PrlUiConstants.Start).IsEnabled);

        Element(window, PrlUiConstants.RunKindScratch).AsRadioButton().IsChecked = true;
        Assert.True(Gone(PrlUiConstants.BaseRun));
        window.Close();
    }

    /// <summary>
    /// The panel choice and the comparison entry on the owned desktop: both are on screen, a new capture is the chosen panel (so the data check still gates Start),
    /// and Compare stays closed until two runs on one panel are chosen. The store of the test data root is not assumed to be empty or full.
    /// </summary>
    [Fact(Timeout = 180_000)]
    public void ThePanelChoiceAndTheComparisonEntryAreOnScreenAndCompareStaysClosedWithoutTwoRuns()
    {
        LaunchApplication();
        Window window = new MainWindowPage(MainWindow!, this).OpenPortfolioRlWindow();
        foreach (string id in new[] { PrlUiConstants.StoredPanel, PrlUiConstants.CompareRunA, PrlUiConstants.CompareRunB, PrlUiConstants.Compare })
            Element(window, id);
        Assert.False(Element(window, PrlUiConstants.Compare).IsEnabled);
        Assert.False(Element(window, PrlUiConstants.Start).IsEnabled);                  // a new capture without sources and a period is not a feasible run
        Assert.Null(window.FindFirstDescendant(cf => cf.ByAutomationId(PrlUiConstants.StoredPanelMessage)));    // the default panel has no check line
        Assert.Null(window.FindFirstDescendant(cf => cf.ByAutomationId(PrlUiConstants.ComparisonMessage)));
        window.Close();
    }
}
