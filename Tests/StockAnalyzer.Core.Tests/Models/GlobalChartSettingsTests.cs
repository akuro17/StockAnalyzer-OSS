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
}
