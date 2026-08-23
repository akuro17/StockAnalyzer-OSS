// --------------------------------------------------------------------------------
// File: Tests/IndicatorManagementTests.cs
// --------------------------------------------------------------------------------
using StockAnalyzer.UITests.Infrastructure;
using StockAnalyzer.UITests.PageObjects;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.UITests.Tests
{
    [Collection("UI Tests")]
    public class IndicatorManagementTests : UITestBase
    {
        public IndicatorManagementTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void AddIndicator_SMA_VerifiesContentPresence()
        {
            try
            {
                LaunchApplication();
                var mainPage = new MainWindowPage(MainWindow!, this);
                int beforeCount = mainPage.GetChartChildCount();

                var dialog = mainPage.OpenAddIndicatorDialog();
                var settingsPage = new SettingsDialogPage(dialog, this);
                
                settingsPage.AddIndicator("Simple Moving Average", "20");

                mainPage.VerifyIndicatorAdded("Sma", 20, beforeCount);
            }
            catch
            {
                CaptureScreenshotOnFailure(nameof(AddIndicator_SMA_VerifiesContentPresence));
                throw;
            }
        }
    }
}
