using System.Text.Json;
using Xunit;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Settings;

namespace StockAnalyzer.Core.Tests.Models;

public class GlobalChartSettingsTests
{
    [Fact]
    public void EnableExtendedLookbackForIndicators_DefaultsToFalse()
    {
        var settings = new GlobalChartSettings();

        Assert.False(settings.EnableExtendedLookbackForIndicators);
    }

    [Fact]
    public void EnableExtendedLookbackForIndicators_SurvivesJsonRoundTrip()
    {
        var settings = new GlobalChartSettings { EnableExtendedLookbackForIndicators = true };

        var json = JsonSerializer.Serialize(settings, GlobalChartSettingsJsonContext.Default.GlobalChartSettings);
        var deserialized = JsonSerializer.Deserialize(json, GlobalChartSettingsJsonContext.Default.GlobalChartSettings);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.EnableExtendedLookbackForIndicators);
    }

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
    public void Seasonality_DefaultsToFourteenPointFontsAndTheStartingPalette()
    {
        var settings = new GlobalChartSettings();

        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityFontSize, settings.SeasonalityLegendFontSize);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityFontSize, settings.SeasonalityAxisFontSize);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityFontSize, settings.SeasonalityMonthlyTableFontSize);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityMonthlyUpColor, settings.SeasonalityMonthlyUpColor);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityMonthlyDownColor, settings.SeasonalityMonthlyDownColor);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityMonthlyNeutralColor, settings.SeasonalityMonthlyNeutralColor);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityYearColor1, settings.SeasonalityYearColor1);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityYearColor8, settings.SeasonalityYearColor8);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityYearColor9, settings.SeasonalityYearColor9);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityYearColor10, settings.SeasonalityYearColor10);
        // #FF3399 (pink) was the explicit user-requested value for slot 6; pin it literally so an
        // accidental edit to the constant is caught.
        Assert.Equal("#FF3399", settings.SeasonalityYearColor6);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityYearsToOverlay, settings.SeasonalityYearsToOverlay);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityLineThickness, settings.SeasonalityLineThickness);
    }

    [Theory]
    [InlineData(0u, SeasonalityChartConstants.MinYearsToOverlay)]
    [InlineData(500u, SeasonalityChartConstants.MaxYearsToOverlay)]
    [InlineData(37u, 37u)]
    public void Validate_ClampsSeasonalityYearsToOverlayIntoTheSelectableRange(uint input, uint expected)
    {
        var validated = new GlobalChartSettings { SeasonalityYearsToOverlay = input }.Validate();

        Assert.Equal(expected, validated.SeasonalityYearsToOverlay);
    }

    [Theory]
    [InlineData(0.1f, 0.5f)]
    [InlineData(9f, 5f)]
    [InlineData(2.5f, 2.5f)]
    public void Validate_ClampsSeasonalityLineThicknessIntoTheSelectableRange(float input, float expected)
    {
        var validated = new GlobalChartSettings { SeasonalityLineThickness = input }.Validate();

        Assert.Equal(expected, validated.SeasonalityLineThickness);
    }

    [Fact]
    public void DrawingLinkMode_DefaultsToSnapToParentAnchorPoint()
    {
        Assert.Equal(DrawingLinkMode.SnapToParentAnchorPoint, new GlobalChartSettings().DrawingLinkMode);
    }

    [Fact]
    public void DrawingLinkMode_MissingFromJson_FallsBackToSnapToParentAnchorPoint()
    {
        var back = JsonSerializer.Deserialize("{}", GlobalChartSettingsJsonContext.Default.GlobalChartSettings);

        Assert.NotNull(back);
        Assert.Equal(DrawingLinkMode.SnapToParentAnchorPoint, back!.DrawingLinkMode);
    }

    [Fact]
    public void DrawingLinkMode_SurvivesJsonRoundTrip()
    {
        var settings = new GlobalChartSettings { DrawingLinkMode = DrawingLinkMode.PreserveRelativePosition };

        var json = JsonSerializer.Serialize(settings, GlobalChartSettingsJsonContext.Default.GlobalChartSettings);
        var back = JsonSerializer.Deserialize(json, GlobalChartSettingsJsonContext.Default.GlobalChartSettings);

        Assert.NotNull(back);
        Assert.Equal(DrawingLinkMode.PreserveRelativePosition, back!.DrawingLinkMode);
    }

    [Fact]
    public void DrawingLinkMode_UndefinedValue_IsHealedToSnapToParentAnchorPointByValidate()
    {
        var validated = new GlobalChartSettings { DrawingLinkMode = (DrawingLinkMode)999 }.Validate();

        Assert.Equal(DrawingLinkMode.SnapToParentAnchorPoint, validated.DrawingLinkMode);
    }

    [Fact]
    public void DrawingLinkMode_DefinedValue_IsKeptByValidate()
    {
        var validated = new GlobalChartSettings { DrawingLinkMode = DrawingLinkMode.PreserveRelativePosition }.Validate();

        Assert.Equal(DrawingLinkMode.PreserveRelativePosition, validated.DrawingLinkMode);
    }

    [Fact]
    public void Seasonality_SurvivesJsonRoundTrip()
    {
        var settings = new GlobalChartSettings
        {
            SeasonalityAxisFontSize = 19f,
            SeasonalityMonthlyUpColor = "#FF010203",
            SeasonalityYearColor5 = "#FF0A0B0C"
        };

        var json = JsonSerializer.Serialize(settings, GlobalChartSettingsJsonContext.Default.GlobalChartSettings);
        var back = JsonSerializer.Deserialize(json, GlobalChartSettingsJsonContext.Default.GlobalChartSettings);

        Assert.NotNull(back);
        Assert.Equal(19f, back!.SeasonalityAxisFontSize);
        Assert.Equal("#FF010203", back.SeasonalityMonthlyUpColor);
        Assert.Equal("#FF0A0B0C", back.SeasonalityYearColor5);
    }

    [Fact]
    public void Validate_ShouldClampSeasonalityFontSizes()
    {
        var settings = new GlobalChartSettings
        {
            SeasonalityLegendFontSize = 2f,
            SeasonalityAxisFontSize = 99f,
            SeasonalityMonthlyTableFontSize = 16f
        };

        var validated = settings.Validate();

        Assert.Equal(ChartSettingsConstants.MinDrawingFontSize, validated.SeasonalityLegendFontSize);
        Assert.Equal(ChartSettingsConstants.MaxDrawingFontSize, validated.SeasonalityAxisFontSize);
        Assert.Equal(16f, validated.SeasonalityMonthlyTableFontSize);
    }

    [Fact]
    public void Validate_ShouldHealMalformedSeasonalityColors()
    {
        var settings = new GlobalChartSettings
        {
            SeasonalityMonthlyUpColor = "not-a-color",
            SeasonalityMonthlyDownColor = "",
            SeasonalityMonthlyNeutralColor = "rgb(1,2,3)",
            SeasonalityYearColor3 = "#GGGGGG",
            SeasonalityYearColor10 = "light-purple"
        };

        var validated = settings.Validate();

        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityMonthlyUpColor, validated.SeasonalityMonthlyUpColor);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityMonthlyDownColor, validated.SeasonalityMonthlyDownColor);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityMonthlyNeutralColor, validated.SeasonalityMonthlyNeutralColor);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityYearColor3, validated.SeasonalityYearColor3);
        Assert.Equal(ChartSettingsConstants.DefaultSeasonalityYearColor10, validated.SeasonalityYearColor10);
    }

    [Fact]
    public void Validate_ShouldPreserveCustomSeasonalityColors()
    {
        var settings = new GlobalChartSettings
        {
            SeasonalityMonthlyUpColor = "#FF102030",
            SeasonalityYearColor2 = "#AABBCCDD"
        };

        var validated = settings.Validate();

        Assert.Equal("#FF102030", validated.SeasonalityMonthlyUpColor);
        Assert.Equal("#AABBCCDD", validated.SeasonalityYearColor2);
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

