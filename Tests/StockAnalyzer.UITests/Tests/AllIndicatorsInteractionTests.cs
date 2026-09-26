using FlaUI.Core.AutomationElements;
using StockAnalyzer.UITests.PageObjects;
using System;
using System.Collections.Generic;
using System.Text;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using System.Linq;
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
                
                var indicatorTypes = IndicatorFactory.GetRegisteredTypesStatic()
                    .Where(type => type != IndicatorType.Price)
                    .OrderBy(type => type.ToString())
                    .ToArray();
                Assert.NotEmpty(indicatorTypes);

                var dialogElement = mainPage.OpenAddIndicatorDialog();
                var settingsPage = new SettingsDialogPage(dialogElement, this);
                var sbErrors = new StringBuilder();
                int successCount = 0;

                foreach (var type in indicatorTypes)
                {
                    var name = type.ToString();
                    try
                    {
                        settingsPage.AddIndicator(name);
                        successCount++;
                        LogInfo($"Added {name}");
                    }
                    catch (Exception ex)
                    {
                        LogInfo($"Failed {name}: {ex}");
                        sbErrors.AppendLine($"Failed {name}: {ex.Message}");
                    }
                }

                settingsPage.Close();
                LogInfo($"Completed indicator interaction test: {successCount}/{indicatorTypes.Length} added.");
                
                if (sbErrors.Length > 0)
                {
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
