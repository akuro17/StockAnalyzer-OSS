using SkiaSharp;
using StockAnalyzer.Avalonia.Views.Chart.Renderers;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Avalonia.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Views.Chart.Renderers;

public class AxisLabelRequestTests
{
    [Fact]
    public void Constructor_ShouldInitializePropertiesCorrectly()
    {
        // Arrange
        decimal expectedValue = 123.45m;
        SKColor expectedColor = SKColors.Blue;
        string expectedText = "123.45";
        AxisLabelStyle expectedStyle = AxisLabelStyle.Default;

        // Act
        var request = new AxisLabelRequest(expectedValue, expectedColor, expectedText, expectedStyle);

        // Assert
        Assert.Equal(expectedValue, request.Value);
        Assert.Equal(expectedColor, request.Color);
        Assert.Equal(expectedText, request.Label);
        Assert.Equal(expectedStyle, request.Style);
    }
}
