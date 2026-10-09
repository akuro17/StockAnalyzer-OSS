using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Services.ReinforcementLearning;
using StockAnalyzer.UITests.PageObjects;
using Xunit;

namespace StockAnalyzer.UITests.Tests;

public sealed partial class RlStoredPriceDesktopTests
{
    [Fact(Timeout = 180_000)]
    public void U01_OwnedDesktopEmptyListsNamedLimitationsAndInvalidNumericAdmission()
    {
        LaunchApplication(root =>
        {
            using var registry = new ModelGenerationRegistry();
            registry.ConfigureRlArtifacts(root, new RlArtifactSettings());
            registry.SaveRlArtifactPolicyAsync(new()).GetAwaiter().GetResult();
        });
        var window = new MainWindowPage(MainWindow!, this).OpenRlTrainingWindow();
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable(EvidenceDirectoryVariable) ?? Path.GetTempPath(), "u01_desktop_initial_automation.json"),
            JsonSerializer.Serialize(window.FindAllDescendants().Select(e => new { id = e.Properties.AutomationId.ValueOrDefault, name = e.Properties.Name.ValueOrDefault, offscreen = e.IsOffscreen, type = e.Properties.ControlType.ValueOrDefault.ToString(), bounds = e.BoundingRectangle.ToString() })));
        SaveScreenshot(window, "sa_RL_U01_desktop_initial.png");
        WaitUntil(() => !Element(window, RlUiConstants.Train).IsEnabled);
        foreach (string id in new[] { RlUiConstants.Models, RlUiConstants.FrozenDirection, RlUiConstants.Blocks,
            RlUiConstants.Policies, RlUiConstants.Cancel, RlUiConstants.Pause, RlUiConstants.Activate })
            Assert.DoesNotContain(window.FindAllDescendants(), e => e.Properties.AutomationId.ValueOrDefault == id && !e.IsOffscreen);
        var scroll = window.FindAllDescendants().FirstOrDefault(e => e.Patterns.Scroll.IsSupported && e.Patterns.Scroll.Pattern.VerticallyScrollable);
        void ScrollOwned(bool up)
        {
            if (scroll is not null) scroll.Patterns.Scroll.Pattern.Scroll(ScrollAmount.NoAmount, up ? ScrollAmount.LargeDecrement : ScrollAmount.LargeIncrement);
            else
            {
                window.Focus(); var bounds = window.BoundingRectangle;
                Mouse.MoveTo((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
                Mouse.Scroll(up ? CheckpointScrollSteps : -CheckpointScrollSteps);
            }
        }
        AutomationElement ScrolledElement(string id)
        {
            if (scroll is not null) scroll.Patterns.Scroll.Pattern.SetScrollPercent(-1, 0);
            return WaitForElement(() =>
            {
                var found = window.FindFirstDescendant(cf => cf.ByAutomationId(id));
                if (found is not null) return found;
                ScrollOwned(false);
                return null;
            }, errorMessage: "Owned RL element not available after scrolling: " + id);
        }
        var settings = ScrolledElement(RlUiConstants.StorageSettings);
        void Reveal(AutomationElement element)
        {
            if (element.Patterns.ScrollItem.IsSupported) element.Patterns.ScrollItem.Pattern.ScrollIntoView();
            WaitUntil(() =>
            {
                var bounds = element.BoundingRectangle;
                if (!element.IsOffscreen && bounds.Top >= window.BoundingRectangle.Top && bounds.Bottom <= window.BoundingRectangle.Bottom) return true;
                ScrollOwned(bounds.Top < window.BoundingRectangle.Top);
                return false;
            }, errorMessage: "Owned RL control could not be brought into the viewport");
        }
        Reveal(settings);
        if (settings.Patterns.ExpandCollapse.IsSupported) settings.Patterns.ExpandCollapse.Pattern.Expand(); else settings.Click();
        AutomationElement Editor(string id) => WaitForElement(() =>
        {
            var control = window.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? window.FindFirstDescendant(cf => cf.ByAutomationId(id + RlUiConstants.NumericEditorSuffix));
            if (control is null) ScrollOwned(false);
            return control?.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit)) ?? control;
        });
        void Number(string id, string text)
        {
            var editor = Editor(id); Reveal(editor);
            editor.AsTextBox().Text = text; editor.Focus(); Keyboard.Press(VirtualKeyShort.TAB);
        }
        Number(RlUiConstants.StorageNonactiveDays, "0"); Number(RlUiConstants.StorageKeepCount, "1");
        WaitUntil(() => Element(window, RlUiConstants.StorageSave).IsEnabled);
        Invoke(window, RlUiConstants.StorageSave);
        WaitUntil(() => Localized("Rl.Storage.Ready").Any(t => window.FindAllDescendants().Any(e => e.Properties.Name.ValueOrDefault == t)));
        Reveal(Element(window, RlUiConstants.TrainingSetupSection));
        Text(window, RlUiConstants.Symbol, "AAPL"); Text(window, RlUiConstants.StartDate, "2000-01-01");
        Text(window, RlUiConstants.EndDate, "2026-01-01");
        WaitUntil(() => Element(window, RlUiConstants.Train).IsEnabled);
        Number(RlUiConstants.Capital, ""); WaitUntil(() => !Element(window, RlUiConstants.Train).IsEnabled);
        Number(RlUiConstants.Capital, "999999999999999999999999999999999999"); Assert.False(Element(window, RlUiConstants.Train).IsEnabled);
        Reveal(ScrolledElement(RlUiConstants.VerificationSection));
        foreach (string fact in new[] { "F01", "F02", "F03" })
            Assert.Contains(window.FindAllDescendants(), e => Localized("Rl.Ui.Fact." + fact + ".Label").Contains(e.Properties.Name.ValueOrDefault));
        var ordered = new[] { RlUiConstants.TrainingSetupSection, RlUiConstants.CostsSection, RlUiConstants.TrainingSection,
            RlUiConstants.ModelsSection, RlUiConstants.StorageSettings, RlUiConstants.MaintenanceSection, RlUiConstants.VerificationSection };
        var nodes = ordered.Select(ScrolledElement).ToArray();
        Assert.All(nodes, n => Assert.False(n.BoundingRectangle.IsEmpty));
        File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable(EvidenceDirectoryVariable) ?? Path.GetTempPath(), "u01_desktop_automation.json"),
            JsonSerializer.Serialize(window.FindAllDescendants().Select(e => new { id = e.Properties.AutomationId.ValueOrDefault, name = e.Properties.Name.ValueOrDefault, offscreen = e.IsOffscreen, enabled = e.IsEnabled, bounds = e.BoundingRectangle.ToString() })));
        SaveScreenshot(window, "sa_RL_U01_desktop.png");
    }
}
