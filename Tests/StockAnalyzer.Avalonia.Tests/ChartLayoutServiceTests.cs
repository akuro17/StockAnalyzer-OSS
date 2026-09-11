using Avalonia;
using StockAnalyzer.Avalonia.Views.Chart;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;
using System.Collections.Generic;
using System.Drawing; // For Color if needed, but likely SKColor or Avalonia Color

namespace StockAnalyzer.Avalonia.Tests
{
    public class ChartLayoutServiceTests
    {
        [Fact]
        public void CreateLayout_Candlestick_AllocatesPanels()
        {
            // Arrange
            var bounds = new Rect(0, 0, 1000, 1000);
            var chartType = ChartType.Candlestick;
            var indicators = new List<CoreIndicatorSettings>
            {
                new CoreIndicatorSettings { IsOverlay = false, IsEnabled = true }
            };

            // Act
            var layout = ChartLayoutService.CreateLayout(bounds, chartType, indicators);

            // Assert
            Assert.Single(layout.PanelAreas);
        }

        [Theory]
        [InlineData(ChartType.Renko)]
        [InlineData(ChartType.ReverseWatch)]
        public void CreateLayout_NonIndicatorChart_DoesNotAllocatePanels(ChartType chartType)
        {
            // Arrange
            var bounds = new Rect(0, 0, 1000, 1000);
            var indicators = new List<CoreIndicatorSettings>
            {
                new CoreIndicatorSettings { IsOverlay = false, IsEnabled = true }
            };

            // Act
            var layout = ChartLayoutService.CreateLayout(bounds, chartType, indicators);

            // Assert
            // These chart types set SupportsIndicators = false, so the entire indicator pass is
            // skipped and no panel is allocated even with an enabled non-overlay indicator.
            // See SA_UI_INTERACTION.md section 27.
            Assert.Empty(layout.PanelAreas);
        }

        [Fact]
        public void CreateLayout_HeikinAshi_AllocatesPanels()
        {
           // Arrange
            var bounds = new Rect(0, 0, 1000, 1000);
            var chartType = ChartType.HeikinAshi;
            var indicators = new List<CoreIndicatorSettings>
            {
                new CoreIndicatorSettings { IsOverlay = false, IsEnabled = true }
            };

            // Act
            var layout = ChartLayoutService.CreateLayout(bounds, chartType, indicators);

            // Assert
            Assert.Single(layout.PanelAreas);
        }
        [Fact]
        public void CreateLayout_WithMultiplePanels_AddsGaps()
        {
            // Arrange
            var bounds = new Rect(0, 0, 1000, 1000);
            var chartType = ChartType.Candlestick;
            var indicators = new List<CoreIndicatorSettings>
            {
                new CoreIndicatorSettings { IsOverlay = false, IsEnabled = true },
                new CoreIndicatorSettings { IsOverlay = false, IsEnabled = true }
            };

            // Act
            var layout = ChartLayoutService.CreateLayout(bounds, chartType, indicators);

            // Assert
            Assert.Equal(2, layout.PanelAreas.Count);
            
            var panel1 = layout.PanelAreas[0];
            var panel2 = layout.PanelAreas[1];
            
            // Gap should be 10
            var gap = panel2.Y - panel1.Bottom;
            Assert.Equal(10, gap);
        }
    }
}
