using SkiaSharp;
using StockAnalyzer.Core.Theme;
using StockAnalyzer.Core.Utilities;
using StockAnalyzer.Core.Models;
using System.Text.Json;
using Xunit;

namespace StockAnalyzer.Core.Tests;

public class ThemePersistenceTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new SKColorJsonConverter() }
    };

    [Fact]
    public void SKColorJsonConverter_ShouldSerializeAndDeserializeCorrectly()
    {
        // Arrange
        var color = new SKColor(255, 128, 64, 200);
        
        // Act
        string json = JsonSerializer.Serialize(color, _jsonOptions);
        var deserializedColor = JsonSerializer.Deserialize<SKColor>(json, _jsonOptions);

        // Assert
        Assert.Equal(color, deserializedColor);
        Assert.Contains("#C8FF8040", json);
    }

    [Fact]
    public void ThemeColors_ShouldRoundTripThroughJson()
    {
        // Arrange
        var original = ThemeColors.Dark with 
        { 
            ChartBackground = IndicatorColor.FromRgb(0, 0, 255), // Blue
            Bullish = IndicatorColor.FromRgb(255, 255, 0) // Yellow
        };

        // Act
        string json = JsonSerializer.Serialize(original, _jsonOptions);
        var deserialized = JsonSerializer.Deserialize<ThemeColors>(json, _jsonOptions);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal(original.ChartBackground, deserialized.ChartBackground);
        Assert.Equal(original.Bullish, deserialized.Bullish);
        Assert.Equal(original.Bearish, deserialized.Bearish); // Should maintain other fields
    }
}
