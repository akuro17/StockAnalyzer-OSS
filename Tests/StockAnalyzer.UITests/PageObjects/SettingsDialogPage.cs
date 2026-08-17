// --------------------------------------------------------------------------------
// File: PageObjects/SettingsDialogPage.cs
// --------------------------------------------------------------------------------
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using StockAnalyzer.UITests.Infrastructure;
using StockAnalyzer.UITests.Helpers; // Added
using System;
using System.Threading;

namespace StockAnalyzer.UITests.PageObjects
{
    public class SettingsDialogPage
    {
        private readonly AutomationElement _dialog;
        private readonly UITestBase _testBase;

        public SettingsDialogPage(AutomationElement dialog, UITestBase testBase)
        {
            _dialog = dialog ?? throw new ArgumentNullException(nameof(dialog));
            _testBase = testBase ?? throw new ArgumentNullException(nameof(testBase));
        }

        public void AddIndicator(string indicatorName, string? periodStr = null)
        {
            // 1. Click "Add" (追加)
            var addButton = _testBase.WaitForElement(() => 
                _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Settings_AddButton")),
                errorMessage: "Add button not found");
            
            _dialog.Focus();
            Thread.Sleep(200);

            var invokePattern = addButton.Patterns.Invoke.PatternOrDefault;
            if (invokePattern != null)
            {
                invokePattern.Invoke();
            }
            else
            {
                addButton.Click();
            }
            
            // Give time for menu to open
            Thread.Sleep(800);

            // 2. Select Indicator from Menu (Generic Search or Map-based)
            // 2. Select Indicator from Menu (Generic Search or Map-based)
            if (IndicatorMenuMap.Map.TryGetValue(indicatorName, out var path))
            {
                // Follow the path
                foreach (var part in path)
                {
                    _testBase.LogInfo($"Navigating path part: '{part}'");
                    var item = WaitForMenuItem(part);
                    if (item == null) 
                    {
                        throw new Exception($"Menu path item '{part}' not found for '{indicatorName}'.");
                    }
                    item.Click();
                    Thread.Sleep(200); // Optimized wait for submenu animation
                }
            }
            else
            {
                // Fallback: Search blindly
                var menuItem = WaitForMenuItem(indicatorName);
                if (menuItem == null)
                {
                     throw new Exception($"Indicator menu item '{indicatorName}' not found.");
                }
                menuItem.Click();
            }

            // 3. Set Period if provided
            if (periodStr != null)
            {
                // Find TextBox in right panel. We added AutomationId="Settings_PeriodTextBox"
                var periodBox = _testBase.WaitForElement(() => 
                    _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Settings_PeriodTextBox"))?.AsTextBox(),
                    errorMessage: "Period text box not found (Make sure indicator is selected)");
                
                // Verify it's editable
                periodBox.Text = periodStr;
            }

            // 4. Click OK (Currently we verify adding, so maybe we shouldn't close immediately if we want to verify the list?)
            // The test asks to "Verify Add works". 
            // In the PageObject, let's keep it simple: Add and Close.
        }
        
        public void Close()
        {
             var okButton = _testBase.WaitForElement(() => 
                _dialog.FindFirstDescendant(cf => cf.ByAutomationId("Settings_OkButton").Or(cf.ByName("OK"))).AsButton(),
                errorMessage: "OK button not found");
            
            _dialog.Focus();
            Thread.Sleep(200);

            // In Avalonia, InvokePattern can be unreliable. 
            // Now that we have the exact AutomationId, a simulated physical mouse click is safest.
            okButton.Click();

            // Window might take a moment to close
            Thread.Sleep(500);

            _testBase.WaitUntil(() => 
            {
                try
                {
                    // More reliable in Avalonia: check if window is actually removed from Desktop
                    var desktop = _testBase.Automation!.GetDesktop();
                    var allWindows = desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window));
                    bool stillExists = allWindows.Any(w => w.Name != null && (w.Name.Contains("Settings") || w.Name.Contains("設定")));
                    
                    return !stillExists;
                }
                catch
                {
                    return true; // if accessing throws, it's definitively gone
                }
            }, errorMessage: "Settings dialog failed to close.");
        }

        private void SelectSmaFromMenu()
        {
            // Strategy: Navigate tiers.
            // Using a simple retry since menus might take a split second to appear (fade in etc).
            
            var menu1 = WaitForMenuItem("トレンド系 (Trend Following)");
            menu1.Click();
            
            var menu2 = WaitForMenuItem("移動平均線ファミリー (Moving Averages)");
            menu2.Click();

            var menu3 = WaitForMenuItem("Simple Moving Average");
            menu3.Click();
        }

        private AutomationElement? WaitForMenuItem(string nameFragment)
        {
            var desktop = _testBase.Automation!.GetDesktop();
            
            return _testBase.WaitForElement(() => 
            {
                // 1. Find the ContextMenu (It appears as a Window, Menu, or Pane control type at top level)
                var popups = desktop.FindAllChildren(cf => 
                    cf.ByControlType(ControlType.Window)
                    .Or(cf.ByControlType(ControlType.Menu))
                    .Or(cf.ByControlType(ControlType.Pane))
                    .Or(cf.ByClassName("PopupRoot")));
                
                foreach (var popup in popups)
                {
                    var item = popup.FindFirstDescendant(cf => 
                        cf.ByName(nameFragment).And(cf.ByControlType(ControlType.Button).Or(cf.ByControlType(ControlType.MenuItem))));
                    if (item != null) return item;
                }
                
                // Fallback: search inside the dialog itself
                var fallback = _dialog.FindFirstDescendant(cf => 
                    cf.ByName(nameFragment).And(cf.ByControlType(ControlType.Button).Or(cf.ByControlType(ControlType.MenuItem))));
                return fallback;

            }, timeoutMs: 2000, errorMessage: $"Menu item or button '{nameFragment}' not found.");
        }
    }
}
