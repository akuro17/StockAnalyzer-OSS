using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.UITests.Infrastructure;
using StockAnalyzer.UITests.PageObjects;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.UITests.Tests;

[Collection("UI Tests")]
public sealed class LegacyResultExperienceDesktopTests : UITestBase
{
    private const string FixtureSymbol = "3036-T";
    private const int CompletionTimeoutMs = 120_000;
    private const int ConfigurationScrollSteps = 20;
    private const string TradeHistoryFileName = "backtest_trades_legacy_acceptance.csv";
    private const int CancellationBootstrapIterations = 1_000_000;

    public LegacyResultExperienceDesktopTests(ITestOutputHelper output) : base(output) { }

    [Fact(Timeout = 240_000)]
    public void LegacyResult_CanCalculateInspectExportAndCancel_UsingIsolatedData()
    {
        try
        {
            LaunchApplication();
            Assert.True(File.Exists(Path.Combine(IsolatedDataRoot, "Daily", FixtureSymbol + ".parquet")));

            Window backtest = new MainWindowPage(MainWindow!, this).OpenBacktestWindow();
            var symbol = WaitForElement(() => backtest.FindFirstDescendant(cf =>
                cf.ByAutomationId("Backtest_Symbol"))
                ?? FindEditorBesideLabel(backtest, ControlType.Edit, LocalizedValues("Backtest_Field_Symbol"))
                ?? FindEditorBelowLabel(backtest, ControlType.Edit, LocalizedValues("Backtest_Field_Symbol")),
                errorMessage: "Backtest symbol editor is missing");
            symbol.AsTextBox().Text = FixtureSymbol;
            symbol.Focus();
            Keyboard.Press(VirtualKeyShort.TAB);
            AddEntryCondition(backtest);

            Button run = Button(backtest, "Backtest_RunButton");
            run.Click();
            WaitForState(backtest, "Completed", CompletionTimeoutMs);

            WaitForElement(() => backtest.FindFirstDescendant(cf =>
                cf.ByAutomationId("Backtest_Tab_Results")), errorMessage: "Results tab is missing").Click();
            // The custom-drawn equity curve and Expander headers are visible in the desktop screenshot,
            // but Avalonia does not currently expose them as separate UIA descendants.
            // The Results tab has no mode-description or fingerprint element; the run identity is the first row of the Evaluation Audit section.
            string firstFingerprint = WaitForRunIdentity(backtest, "Run identity is missing");

            string exportedTradesPath = ExportTradeHistoryCsv(backtest);
            string[] csvLines = File.ReadAllLines(exportedTradesPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();
            Assert.True(csvLines.Length > 1, "The completed Legacy run should export a header and at least one real condition-driven trade.");

            WaitForElement(() => backtest.FindFirstDescendant(cf =>
                cf.ByAutomationId("Backtest_Tab_Configuration")), errorMessage: "Configuration tab is missing").Click();
            var bounds = backtest.BoundingRectangle;
            Mouse.MoveTo((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
            Mouse.Scroll(-ConfigurationScrollSteps);
            var iterations = WaitForElement(() => backtest.FindFirstDescendant(cf =>
                cf.ByAutomationId("Backtest_BootstrapIterations"))
                ?? FindEditorBesideLabel(backtest, ControlType.Spinner,
                    LocalizedValues("Backtest_Field_BootstrapIterations"))
                ?? FindEditorBesideLabel(backtest, ControlType.Edit,
                    LocalizedValues("Backtest_Field_BootstrapIterations"))
                ?? FindEditorBelowLabel(backtest, ControlType.Edit,
                    LocalizedValues("Backtest_Field_BootstrapIterations")),
                errorMessage: "Bootstrap iteration editor is missing");
            if (iterations.ControlType == ControlType.Spinner)
                iterations.AsSpinner().Value = CancellationBootstrapIterations;
            else
                iterations.AsTextBox().Text = CancellationBootstrapIterations.ToString(CultureInfo.InvariantCulture);
            Keyboard.Press(VirtualKeyShort.TAB);

            run.Click();
            Button cancel = Button(backtest, "Backtest_CancelButton");
            WaitUntil(() => !run.IsEnabled && cancel.IsEnabled, 10_000,
                "The second run never reached the cancellable Running state");
            cancel.Click();
            WaitForState(backtest, "Cancelled", CompletionTimeoutMs);
            WaitForElement(() => backtest.FindFirstDescendant(cf =>
                cf.ByAutomationId("Backtest_Tab_Results")), errorMessage: "Results tab is missing after cancel").Click();
            Assert.Equal(firstFingerprint, WaitForRunIdentity(backtest, "Previously completed result disappeared after cancel"));
            Assert.True(File.Exists(exportedTradesPath));
        }
        catch
        {
            CaptureScreenshotOnFailure(nameof(LegacyResult_CanCalculateInspectExportAndCancel_UsingIsolatedData));
            throw;
        }
    }

    /// <summary>The run identity (SHA-256) shown in the value cell beside the "Run identity" label of the Evaluation Audit section of the Results tab.</summary>
    private string WaitForRunIdentity(Window backtest, string errorMessage)
    {
        string[] label = LocalizedValues("Backtest_Audit_RunIdentity");
        var cell = WaitForElement(() => FindEditorBesideLabel(backtest, ControlType.Text, label)
            ?? FindEditorBesideLabel(backtest, ControlType.Edit, label), errorMessage: errorMessage);
        string identity = cell.Name;
        if (string.IsNullOrWhiteSpace(identity) && cell.Patterns.Value.IsSupported) identity = cell.Patterns.Value.Pattern.Value.ValueOrDefault ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(identity));
        return identity;
    }

    private Button Button(Window window, string automationId) => WaitForElement(() =>
        window.FindFirstDescendant(cf => cf.ByAutomationId(automationId))?.AsButton(),
        errorMessage: $"Button {automationId} is missing");

    private static AutomationElement? FindEditorBesideLabel(Window window, ControlType editorType, params string[] names)
    {
        var label = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .FirstOrDefault(element => names.Contains(element.Name, StringComparer.Ordinal));
        if (label == null) return null;

        var labelBounds = label.BoundingRectangle;
        return window.FindAllDescendants(cf => cf.ByControlType(editorType))
            .Where(element =>
            {
                var bounds = element.BoundingRectangle;
                return bounds.Left >= labelBounds.Right
                    && bounds.Top < labelBounds.Bottom
                    && bounds.Bottom > labelBounds.Top;
            })
            .OrderBy(element => element.BoundingRectangle.Left)
            .FirstOrDefault();
    }

    private void AddEntryCondition(Window backtest)
    {
        WaitForElement(() => backtest.FindFirstDescendant(cf =>
            cf.ByAutomationId("Backtest_Tab_Indicator")),
            errorMessage: "Indicator selection tab is missing").Click();
        var search = WaitForElement(() => backtest.FindFirstDescendant(cf =>
            cf.ByAutomationId("Backtest_Condition_Search"))?.AsTextBox(),
            errorMessage: "Condition catalog search is missing");
        search.Text = "SMA";
        var catalog = WaitForElement(() => backtest.FindFirstDescendant(cf =>
            cf.ByAutomationId("Backtest_Condition_Catalog"))?.AsListBox(),
            errorMessage: "Condition catalog is missing");
        var sma = WaitForElement(() => catalog.Items.FirstOrDefault(item =>
            item.FindFirstDescendant(cf => cf.ByName("SMA")) != null),
            errorMessage: "SMA catalog item is missing");
        sma.Click();
        Button add = Button(backtest, "Backtest_Condition_AddEntry");
        WaitUntil(() => add.IsEnabled, errorMessage: "SMA entry condition was not accepted");
        add.Click();
        Button(backtest, "Backtest_Condition_AddExit").Click();
    }

    private void WaitForState(Window window, string stateName, int timeoutMs)
    {
        string[] expected = LocalizedValues("Backtest_State_" + stateName);
        WaitUntil(() =>
        {
            string state = window.FindFirstDescendant(cf => cf.ByAutomationId("Backtest_StateText"))?.Name ?? string.Empty;
            return expected.Contains(state, StringComparer.Ordinal);
        }, timeoutMs, $"Backtest did not reach {stateName}");
    }

    private static string[] LocalizedValues(string key) => new[] { "en", "ja" }.Select(language =>
    {
        string resourceName = string.Format(CultureInfo.InvariantCulture,
            LocalizationManager.LocaleResourceNameFormat, language);
        using Stream stream = typeof(LocalizationManager).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Locale resource {resourceName} is unavailable");
        using var resource = JsonDocument.Parse(stream);
        return resource.RootElement.GetProperty(key).GetString()
            ?? throw new InvalidOperationException($"Locale key {key} is null in {resourceName}");
    }).ToArray();

    /// <summary>The Export button writes the trade history as CSV through a Save File dialog; the file goes to the isolated Exports folder.</summary>
    private string ExportTradeHistoryCsv(Window backtest)
    {
        string exportDirectory = Path.Combine(IsolatedDataRoot, "Exports");
        Directory.CreateDirectory(exportDirectory);
        string targetPath = Path.Combine(exportDirectory, TradeHistoryFileName);
        var export = Button(backtest, "Backtest_Results_ExportButton");
        if (export.Patterns.Invoke.PatternOrDefault is { } invoke) invoke.Invoke();
        else export.Click();

        var savePicker = new MainWindowPage(MainWindow!, this)
            .WaitForProcessWindow(LocalizedValues("Backtest_Export_SaveFileTitle"));

        savePicker.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(targetPath);
        WaitUntil(() => PickerAddressMatches(savePicker, targetPath),
            5_000, "Save dialog did not display the isolated export file path");
        Keyboard.Press(VirtualKeyShort.ENTER);

        WaitUntil(() => File.Exists(targetPath), 15_000, "Exported trade history was not written to the isolated folder");
        return targetPath;
    }

    private static bool PickerAddressMatches(Window picker, string expectedPath) =>
        picker.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Any(edit =>
        {
            try { return string.Equals(edit.AsTextBox().Text, expectedPath, StringComparison.OrdinalIgnoreCase); }
            catch (COMException) { return false; }
            catch (TimeoutException) { return false; }
        });
}
