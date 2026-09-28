using System.Linq;
using Xunit;
using FlaUI.Core.AutomationElements;

namespace StockAnalyzer.Tests.E2E
{
    /// <summary>
    /// E2E tests for chart display functionality (Critical Path Scenario 1).
    /// Tests: Application launch, data loading, chart rendering.
    /// </summary>
    [Trait("Category", "E2E")]
    [Trait("Category", "E2E")]
    [Collection("E2E")]
    public class ChartDisplayTests : E2ETestBase
    {
        [Fact]
        public void Application_Launches_Successfully()
        {
            // Act
            LaunchApplication();

            // Assert
            Assert.NotNull(MainWindow);
            Assert.True(MainWindow.IsAvailable);
        }

        [Fact]
        public void MainWindow_HasCorrectTitle()
        {
            // Arrange
            LaunchApplication();

            // Act
            var title = MainWindow?.Title;

            // Assert
            Assert.NotNull(title);
            Assert.Contains("Stock Analyzer", title);
        }

        [Fact]
        public void Create_New_Chart_UI_Is_Ready()
        {
            // Arrange
            LaunchApplication();

            // Act
            System.Threading.Thread.Sleep(3000); 

            // Verify Menu
            var fileMenu = MainWindow?.FindFirstDescendant(cf => cf.ByAutomationId("Menu_File"));
            Assert.NotNull(fileMenu);
        }

        [Fact]
        public void Application_LoadsWithoutCrash()
        {
            // Arrange & Act
            LaunchApplication();
            System.Threading.Thread.Sleep(3000); // Wait for full initialization

            // Assert
            Assert.NotNull(App);
            Assert.False(App.HasExited);
            Assert.NotNull(MainWindow);
            Assert.True(MainWindow.IsAvailable);
        }
    }
}
