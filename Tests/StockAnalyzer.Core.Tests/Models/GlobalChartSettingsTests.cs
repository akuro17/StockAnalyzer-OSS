using Xunit;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Tests.Models;

public class GlobalChartSettingsTests
{
    [Fact]
    public void Validate_ShouldHealOldRainbowColors_6CharHex()
    {
        // Arrange
        var settings = new GlobalChartSettings
        {
            ReverseWatchPhase1Color = "#FF0000",
            ReverseWatchPhase2Color = "#FFA500",
            ReverseWatchPhase3Color = "#FFFF00",
            ReverseWatchPhase4Color = "#008000",
            ReverseWatchPhase5Color = "#0000FF",
            ReverseWatchPhase6Color = "#4B0082",
            ReverseWatchPhase7Color = "#EE82EE",
            ReverseWatchPhase8Color = "#808080"
        };

        // Act
        var healed = settings.Validate();

        // Assert
        Assert.Equal("#00AA00", healed.ReverseWatchPhase1Color);
        Assert.Equal("#88CC00", healed.ReverseWatchPhase8Color);
    }

    [Fact]
    public void Validate_ShouldHealOldRainbowColors_8CharHex()
    {
        // Arrange
        var settings = new GlobalChartSettings
        {
            ReverseWatchPhase1Color = "#FFFF0000",
            ReverseWatchPhase2Color = "#FFFFA500",
            ReverseWatchPhase3Color = "#FFFFFF00",
            ReverseWatchPhase4Color = "#FF008000",
            ReverseWatchPhase5Color = "#FF0000FF",
            ReverseWatchPhase6Color = "#FF4B0082",
            ReverseWatchPhase7Color = "#FFEE82EE",
            ReverseWatchPhase8Color = "#FF808080"
        };

        // Act
        var healed = settings.Validate();

        // Assert
        Assert.Equal("#00AA00", healed.ReverseWatchPhase1Color);
        Assert.Equal("#88CC00", healed.ReverseWatchPhase8Color);
    }

    [Fact]
    public void Validate_ShouldHealOldRainbowColors_CaseInsensitive()
    {
        // Arrange
        var settings = new GlobalChartSettings
        {
            ReverseWatchPhase1Color = "#ff0000",
            ReverseWatchPhase2Color = "#ffa500",
            ReverseWatchPhase3Color = "#ffff00",
            ReverseWatchPhase4Color = "#008000",
            ReverseWatchPhase5Color = "#0000ff",
            ReverseWatchPhase6Color = "#4b0082",
            ReverseWatchPhase7Color = "#ee82ee",
            ReverseWatchPhase8Color = "#808080"
        };

        // Act
        var healed = settings.Validate();

        // Assert
        Assert.Equal("#00AA00", healed.ReverseWatchPhase1Color);
    }

    [Fact]
    public void Validate_ShouldNotHealIfUserCustomized()
    {
        // Arrange
        var settings = new GlobalChartSettings
        {
            ReverseWatchPhase1Color = "#123456", // Customized
            ReverseWatchPhase2Color = "#FFA500",
            ReverseWatchPhase3Color = "#FFFF00",
            ReverseWatchPhase4Color = "#008000",
            ReverseWatchPhase5Color = "#0000FF",
            ReverseWatchPhase6Color = "#4B0082",
            ReverseWatchPhase7Color = "#EE82EE",
            ReverseWatchPhase8Color = "#808080"
        };

        // Act
        var healed = settings.Validate();

        // Assert
        Assert.Equal("#123456", healed.ReverseWatchPhase1Color); // Should NOT be healed
    }

    [Fact]
    public void Validate_ShouldClampDrawingFontSize()
    {
        // Arrange
        var tooSmall = new GlobalChartSettings { DrawingFontSize = 2.0f };
        var tooLarge = new GlobalChartSettings { DrawingFontSize = 50.0f };
        var valid = new GlobalChartSettings { DrawingFontSize = 16.0f };

        // Act & Assert
        Assert.Equal(8.0f, tooSmall.Validate().DrawingFontSize);
        Assert.Equal(32.0f, tooLarge.Validate().DrawingFontSize);
        Assert.Equal(16.0f, valid.Validate().DrawingFontSize);
    }

    [Fact]
    public void Validate_ShouldFallbackEmptyDrawingColors()
    {
        // Arrange
        var settings = new GlobalChartSettings
        {
            DrawingDefaultColor = "   ",
            DrawingHandleColor = ""
        };

        // Act
        var validated = settings.Validate();

        // Assert
        Assert.Equal(ChartSettingsConstants.DefaultDrawingColor, validated.DrawingDefaultColor);
        Assert.Equal(ChartSettingsConstants.DefaultDrawingHandleColor, validated.DrawingHandleColor);
    }

    [Fact]
    public void Validate_ShouldHealInvalidStrokeThickness()
    {
        // Arrange
        var tooThin = new GlobalChartSettings { DefaultStrokeThickness = 0.05 };
        var tooThick = new GlobalChartSettings { DefaultStrokeThickness = 12.0 };
        var nanThickness = new GlobalChartSettings { DefaultStrokeThickness = double.NaN };
        var posInfinity = new GlobalChartSettings { DefaultStrokeThickness = double.PositiveInfinity };
        var negInfinity = new GlobalChartSettings { DefaultStrokeThickness = double.NegativeInfinity };
        var valid = new GlobalChartSettings { DefaultStrokeThickness = 2.5 };

        // Act & Assert
        Assert.Equal(1.0, tooThin.Validate().DefaultStrokeThickness);
        Assert.Equal(1.0, tooThick.Validate().DefaultStrokeThickness);
        Assert.Equal(1.0, nanThickness.Validate().DefaultStrokeThickness);
        Assert.Equal(1.0, posInfinity.Validate().DefaultStrokeThickness);
        Assert.Equal(1.0, negInfinity.Validate().DefaultStrokeThickness);
        Assert.Equal(2.5, valid.Validate().DefaultStrokeThickness);
    }

    [Theory]
    [InlineData("#INVALID")]
    [InlineData("#123")]
    [InlineData("red")]
    [InlineData("#ABCDEFGH_EXTRA")]
    [InlineData("not_a_color")]
    public void Validate_ShouldHealMalformedDrawingColors(string malformedInput)
    {
        // Arrange
        var settings = new GlobalChartSettings
        {
            DrawingDefaultColor = malformedInput,
            DrawingHandleColor = malformedInput
        };

        // Act
        var validated = settings.Validate();

        // Assert
        Assert.Equal(ChartSettingsConstants.DefaultDrawingColor, validated.DrawingDefaultColor);
        Assert.Equal(ChartSettingsConstants.DefaultDrawingHandleColor, validated.DrawingHandleColor);
    }

    [Theory]
    [InlineData("#00B050")]
    [InlineData("#FF0000")]
    [InlineData("#123456")]
    [InlineData("#FF123456")]
    public void Validate_ShouldPreserveValidDrawingColors(string validHex)
    {
        // Arrange
        var settings = new GlobalChartSettings
        {
            DrawingDefaultColor = validHex,
            DrawingHandleColor = validHex
        };

        // Act
        var validated = settings.Validate();

        // Assert
        Assert.Equal(validHex, validated.DrawingDefaultColor);
        Assert.Equal(validHex, validated.DrawingHandleColor);
    }

    [Fact]
    public void Validate_ShouldClampSmartGuideSnapDistance()
    {
        // Arrange
        var tooSmall = new GlobalChartSettings { SmartGuideSnapDistance = 0.5 };
        var tooLarge = new GlobalChartSettings { SmartGuideSnapDistance = 60.0 };
        var nanDistance = new GlobalChartSettings { SmartGuideSnapDistance = double.NaN };
        var posInfinity = new GlobalChartSettings { SmartGuideSnapDistance = double.PositiveInfinity };
        var negInfinity = new GlobalChartSettings { SmartGuideSnapDistance = double.NegativeInfinity };
        var valid = new GlobalChartSettings { SmartGuideSnapDistance = 12.5, SmartGuidesEnabled = false };

        // Act & Assert
        Assert.Equal(ChartSettingsConstants.DefaultSmartGuideSnapDistance, tooSmall.Validate().SmartGuideSnapDistance);
        Assert.Equal(ChartSettingsConstants.DefaultSmartGuideSnapDistance, tooLarge.Validate().SmartGuideSnapDistance);
        Assert.Equal(ChartSettingsConstants.DefaultSmartGuideSnapDistance, nanDistance.Validate().SmartGuideSnapDistance);
        Assert.Equal(ChartSettingsConstants.DefaultSmartGuideSnapDistance, posInfinity.Validate().SmartGuideSnapDistance);
        Assert.Equal(ChartSettingsConstants.DefaultSmartGuideSnapDistance, negInfinity.Validate().SmartGuideSnapDistance);
        Assert.Equal(12.5, valid.Validate().SmartGuideSnapDistance);
        Assert.False(valid.Validate().SmartGuidesEnabled);
    }
}

