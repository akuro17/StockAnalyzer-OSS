using System;
using System.Text.Json;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Behaviors;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models.HarmonicPattern;
using Xunit;

namespace StockAnalyzer.Tests.Drawing;

public class HarmonicDrawingPivotModeTests
{
    [Fact]
    public void HarmonicPatternObject_DefaultsToLegacy_ForBackwardCompatibility()
    {
        var obj = new HarmonicPatternObject();
        Assert.Equal(HarmonicPivotMode.Legacy, obj.PivotMode);
    }

    [Fact]
    public void HarmonicPatternBehavior_CreateInstance_InitializesToLocalExtremaAmplitude()
    {
        var behavior = new HarmonicPatternBehavior();
        var method = typeof(HarmonicPatternBehavior).GetMethod("CreateInstance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        var obj = (HarmonicPatternObject)method.Invoke(behavior, new object?[] { new ChartPoint(DateTime.Today, 100m), null })!;
        Assert.NotNull(obj);
        Assert.Equal(HarmonicPivotMode.LocalExtremaAmplitude, obj.PivotMode);
    }

    [Fact]
    public void HarmonicPatternObject_JsonDeserialization_WithoutPivotMode_DefaultsToLegacy()
    {
        string legacyJson = "{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"ZigZagThreshold\":3.0}";
        var deserialized = JsonSerializer.Deserialize<HarmonicPatternObject>(legacyJson);
        Assert.NotNull(deserialized);
        Assert.Equal(HarmonicPivotMode.Legacy, deserialized.PivotMode);
    }

    [Fact]
    public void HarmonicPatternObject_JsonSerializationAndDeserialization_RoundTripsPivotMode()
    {
        var obj = new HarmonicPatternObject
        {
            PivotMode = HarmonicPivotMode.LocalExtremaAmplitude
        };

        string json = JsonSerializer.Serialize(obj);
        var deserialized = JsonSerializer.Deserialize<HarmonicPatternObject>(json);
        Assert.NotNull(deserialized);
        Assert.Equal(HarmonicPivotMode.LocalExtremaAmplitude, deserialized.PivotMode);
    }
}
