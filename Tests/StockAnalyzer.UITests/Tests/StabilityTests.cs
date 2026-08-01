// --------------------------------------------------------------------------------
// File: Tests/StabilityTests.cs
// --------------------------------------------------------------------------------
using StockAnalyzer.UITests.Infrastructure;
using StockAnalyzer.UITests.PageObjects;
using Xunit;
using Xunit.Abstractions;

namespace StockAnalyzer.UITests.Tests
{
    [Collection("UI Tests")]
    public class StabilityTests : UITestBase
    {
        public StabilityTests(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void Application_Launches_AndControlsExist()
        {
            try
            {
                LaunchApplication();

                Assert.NotNull(MainWindow);
                Assert.False(MainWindow!.IsOffscreen);
                
                var mainPage = new MainWindowPage(MainWindow, this);
                Assert.NotNull(mainPage.ChartCanvas);
                // Assert.NotNull(mainPage.OpenAddIndicatorDialog); // Function exists
                // Check for existence of a known UI element like "Set" button
                // (Set button is checked implicitly by OpenAddIndicatorDialog but we can check existence here if we exposed it)

            }
            catch
            {
                CaptureScreenshotOnFailure(nameof(Application_Launches_AndControlsExist));
                throw;
            }
        }
    }
}
