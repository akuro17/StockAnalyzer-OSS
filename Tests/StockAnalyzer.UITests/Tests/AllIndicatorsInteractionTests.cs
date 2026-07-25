using FlaUI.Core.AutomationElements;
using StockAnalyzer.UITests.PageObjects;
using System;
using System.Collections.Generic;
using System.Text;
using StockAnalyzer.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.UITests.Tests
{
    [Collection("UI Tests")]
    public class AllIndicatorsInteractionTests : Infrastructure.UITestBase
    {
        public AllIndicatorsInteractionTests(ITestOutputHelper output) : base(output) { }

        [Fact(Timeout = 1200000)] // 20 minutes to handle 124 indicators on potentially slow CI
        public void VerifyAllIndicatorsInteraction_SingleSession()
        {
            // Optimize: Launch ONCE, then iterate.
            try 
            {
                LaunchApplication();
                var mainPage = new MainWindowPage(MainWindow!, this);
                
                // 1. Open Dialog ONCE (or reopen if needed?)
                // If we add and close, we have to reopen.
                // Reopening dialog is much faster than relaunching app.
                
                var sbErrors = new StringBuilder();
                int successCount = 0;
                int skipCount = 0;

                foreach (var name in StockAnalyzer.UITests.Helpers.IndicatorMenuMap.Map.Keys)
                {
                    try
                    {
                        // Open Dialog
                        var dialogElement = mainPage.OpenAddIndicatorDialog();
                        var settingsPage = new SettingsDialogPage(dialogElement, this);
                        
                        // Try adding
                        try 
                        {
                           settingsPage.AddIndicator(name);
                           settingsPage.Close();
                           successCount++;
                           LogInfo($"Success: Added {name}");
                        }
                        catch (Exception ex)
                        {
                            // Log and Skip
                            LogInfo($"Skipping ({name}): {ex.Message}");
                            skipCount++;
                            sbErrors.AppendLine($"Failed {name}: {ex.Message}"); // Track errors but don't fail immediately to allow others
                            // Ensure closed
                            try { settingsPage.Close(); } catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Fatal error for this iteration
                        LogInfo($"CRITICAL ERROR for {name}: \n{ex.ToString()}");
                        sbErrors.AppendLine($"Failed {name}: {ex.Message}");
                        // Try to recover main window focus?
                    }
                }
                
                LogInfo($"Completed interaction test. Success: {successCount}, Skipped: {skipCount}");
                
                if (sbErrors.Length > 0)
                {
                    System.IO.File.WriteAllText("ui_test_errors.txt", sbErrors.ToString());
                    Assert.Fail($"Some indicators failed interaction:\n{sbErrors}");
                }
            }
            catch (Exception)
            {
                CaptureScreenshotOnFailure($"AllIndicatorsInteractionTests_Session");
                throw;
            }
        }
    }
}
