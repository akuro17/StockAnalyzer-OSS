using System;
using SkiaSharp;
using StockAnalyzer.Core.Theme;
using Xunit;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Tests;

public class SemanticRoleTests
{
    [Fact]
    public void GetSemanticColor_ShouldReturnNonTransparentColor_ForAllRoles()
    {
        // Arrange
        var theme = ThemeColors.Light;
        var roles = Enum.GetValues<SemanticRole>();

        foreach (var role in roles)
        {
            // Act
            var color = theme.GetSemanticColor(role);

            // Assert
            Assert.NotEqual(IndicatorColor.Transparent, color);
            Assert.True(color.Alpha > 0, $"Role {role} should not have a fully transparent color.");
        }
    }

    [Theory]
    [InlineData(SemanticRole.Bullish, SemanticRole.Bullish)]
    [InlineData(SemanticRole.Bearish, SemanticRole.Bearish)]
    [InlineData(SemanticRole.Support, SemanticRole.Support)]
    [InlineData(SemanticRole.Resistance, SemanticRole.Resistance)]
    [InlineData(SemanticRole.EntryLong, SemanticRole.EntryLong)]
    [InlineData(SemanticRole.EntryShort, SemanticRole.EntryShort)]
    public void GetSemanticColor_ShouldMatchThemedColor(SemanticRole role, SemanticRole expectedRole)
    {
        // Arrange
        var theme = ThemeColors.Light;
        
        // Act
        var color = theme.GetSemanticColor(role);

        // Assert
        var expectedColor = role switch
        {
            SemanticRole.Bullish => theme.Bullish,
            SemanticRole.Bearish => theme.Bearish,
            SemanticRole.Support => theme.GeometricSupportLine,
            SemanticRole.Resistance => theme.GeometricResistanceLine,
            SemanticRole.EntryLong => theme.CrossMarkerGolden,
            SemanticRole.EntryShort => theme.CrossMarkerDead,
            _ => theme.Neutral
        };
        Assert.Equal(expectedColor, color);
    }

    [Fact]
    public void GetSemanticColor_ShouldFallbackToNeutral_ForInvalidRole()
    {
        // Arrange
        var theme = ThemeColors.Light;
        var invalidRole = (SemanticRole)999;

        // Act
        var color = theme.GetSemanticColor(invalidRole);

        // Assert
        Assert.Equal(theme.Neutral, color);
    }

    [Fact]
    public void SemanticRole_ToLabel_ShouldReturnJapaneseLabels()
    {
        // Arrange & Act & Assert
        Assert.Equal("支持線", SemanticRole.Support.ToLabel());
        Assert.Equal("抵抗線", SemanticRole.Resistance.ToLabel());
        Assert.Equal("強気", SemanticRole.Bullish.ToLabel());
        Assert.Equal("弱気", SemanticRole.Bearish.ToLabel());
        Assert.Equal("買エントリー", SemanticRole.EntryLong.ToLabel());
        Assert.Equal("売エントリー", SemanticRole.EntryShort.ToLabel());
    }

    [Fact]
    public void GetSemanticColor_ShouldWorkInDarkTheme()
    {
        // Arrange
        var theme = ThemeColors.Dark;
        
        // Act
        var bullishColor = theme.GetSemanticColor(SemanticRole.Bullish);
        var bearishColor = theme.GetSemanticColor(SemanticRole.Bearish);

        // Assert
        Assert.Equal(theme.Bullish, bullishColor);
        Assert.Equal(theme.Bearish, bearishColor);
        Assert.NotEqual(IndicatorColor.Transparent, bullishColor);
    }
}
