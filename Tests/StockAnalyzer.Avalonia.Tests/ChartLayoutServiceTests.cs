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

        [Fact]
        public void CreateLayout_Renko_AllocatesPanels_WithIndicator()
        {
            // Arrange
            var bounds = new Rect(0, 0, 1000, 1000);
            var chartType = ChartType.Renko;
            var indicators = new List<CoreIndicatorSettings>
            {
                new CoreIndicatorSettings { IsOverlay = false, IsEnabled = true }
            };

            // Act
            var layout = ChartLayoutService.CreateLayout(bounds, chartType, indicators);

            // Assert
            // Now Renko supports indicators, so it allocates a panel.
            Assert.Single(layout.PanelAreas);
        }

        [Fact]
        public void CreateLayout_ReverseWatch_DoesNotAllocatePanels()
        {
            // Arrange
            var bounds = new Rect(0, 0, 1000, 1000);
            var chartType = ChartType.ReverseWatch;
            var indicators = new List<CoreIndicatorSettings>
            {
                new CoreIndicatorSettings { IsOverlay = false, IsEnabled = true }
            };

            // Act
            var layout = ChartLayoutService.CreateLayout(bounds, chartType, indicators);

            // Assert
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
