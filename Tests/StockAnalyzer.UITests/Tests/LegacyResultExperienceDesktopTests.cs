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
    private const int CancellationBootstrapIterations = 1_000_000;
    private static readonly string[] NativeFolderPickerConfirmNames =
        ["Select Folder", "\u30d5\u30a9\u30eb\u30c0\u30fc\u306e\u9078\u629e"];

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
                ?? FindEditorBesideLabel(backtest, ControlType.Edit, LocalizedValues("Backtest_Field_Symbol")),
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
            var mode = WaitForElement(() => backtest.FindFirstDescendant(cf =>
                cf.ByAutomationId("Backtest_Results_ModeDescription")), errorMessage: "Legacy mode description is missing");
            Assert.Contains("OHLC", mode.Name, StringComparison.OrdinalIgnoreCase);
            // The custom-drawn equity curve and Expander headers are visible in the desktop screenshot,
            // but Avalonia does not currently expose them as separate UIA descendants.
            var fingerprint = WaitForElement(() => backtest.FindFirstDescendant(cf =>
                cf.ByAutomationId("Backtest_Results_Fingerprint")), errorMessage: "Run fingerprint is missing");
            Assert.False(string.IsNullOrWhiteSpace(fingerprint.Name));
            string firstFingerprint = fingerprint.Name;

            string exportedReportPath = ExportToIsolatedFolder(backtest);
            using (var report = JsonDocument.Parse(File.ReadAllText(exportedReportPath)))
            {
                Assert.True(report.RootElement.TryGetProperty("TotalPnL", out _));
                Assert.True(report.RootElement.GetProperty("TotalTrades").GetInt32() > 0,
                    "The completed Legacy report should contain a real condition-driven trade.");
                var exportedFingerprint = report.RootElement.GetProperty("RunFingerprint");
                Assert.True(exportedFingerprint.GetProperty("IsAvailable").GetBoolean());
                string exportedHash = exportedFingerprint.GetProperty("Sha256").GetString()!;
                Assert.Matches("^[0-9A-F]{64}$", exportedHash);
                Assert.Contains(exportedHash, firstFingerprint, StringComparison.Ordinal);
                Assert.False(report.RootElement.TryGetProperty("ExecutionModel", out _));
            }

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
            var retainedFingerprint = WaitForElement(() => backtest.FindFirstDescendant(cf =>
                cf.ByAutomationId("Backtest_Results_Fingerprint")),
                errorMessage: "Previously completed result disappeared after cancel");
            Assert.Equal(firstFingerprint, retainedFingerprint.Name);
            Assert.True(File.Exists(exportedReportPath));
        }
        catch
        {
            CaptureScreenshotOnFailure(nameof(LegacyResult_CanCalculateInspectExportAndCancel_UsingIsolatedData));
            throw;
        }
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
            cf.ByAutomationId("Backtest_Tab_IndicatorSelection")),
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

    private string ExportToIsolatedFolder(Window backtest)
    {
        string exportDirectory = Path.Combine(IsolatedDataRoot, "Exports");
        Directory.CreateDirectory(exportDirectory);
        var export = Button(backtest, "Backtest_Results_ExportButton");
        if (export.Patterns.Invoke.PatternOrDefault is { } invoke) invoke.Invoke();
        else export.Click();

        var folderPicker = new MainWindowPage(MainWindow!, this)
            .WaitForProcessWindow(LocalizedValues("Backtest_Export_SelectFolderTitle"));

        folderPicker.Focus();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_L);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type(exportDirectory);
        WaitUntil(() => PickerAddressMatches(folderPicker, exportDirectory),
            5_000, "Folder picker did not display the isolated export path");
        Keyboard.Press(VirtualKeyShort.ENTER);

        // The breadcrumb must show both the unique test-run folder and Exports before confirmation.
        string ownedFolderName = Path.GetFileName(IsolatedDataRoot);
        WaitUntil(() => PickerBreadcrumbContains(folderPicker, ownedFolderName)
            && PickerBreadcrumbContains(folderPicker, "Exports"),
            10_000, "Folder picker did not navigate to the isolated Exports directory");
        var confirm = WaitForElement(() => folderPicker.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(button => NativeFolderPickerConfirmNames.Contains(button.Name, StringComparer.Ordinal)),
            errorMessage: "Folder picker confirmation button is missing");
        confirm.Click();
        WaitUntil(() => Directory.EnumerateFiles(exportDirectory, "backtest_report_*.json").Any(),
            15_000, "Exported report was not written to the isolated folder");
        return Assert.Single(Directory.EnumerateFiles(exportDirectory, "backtest_report_*.json"));
    }

    private static bool PickerAddressMatches(Window picker, string expectedPath) =>
        picker.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Any(edit =>
        {
            try { return string.Equals(edit.AsTextBox().Text, expectedPath, StringComparison.OrdinalIgnoreCase); }
            catch (COMException) { return false; }
            catch (TimeoutException) { return false; }
        });

    private static bool PickerBreadcrumbContains(Window picker, string folderName)
    {
        double breadcrumbBottom = picker.BoundingRectangle.Top + 100;
        return picker.FindAllDescendants(cf => cf.ByName(folderName))
            .Any(element => element.BoundingRectangle.Top < breadcrumbBottom);
    }
}
