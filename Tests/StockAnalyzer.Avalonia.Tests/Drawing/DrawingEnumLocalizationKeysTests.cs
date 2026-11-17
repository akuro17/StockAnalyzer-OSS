using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Drawing;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>The enum-valued tool parameters need an Enum_{Type}_{Member} key and a Param_{Property} key in both locales.</summary>
public sealed class DrawingEnumLocalizationKeysTests
{
    private static JsonElement LoadLocale(string code)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }
        Assert.NotNull(root);
        using var stream = File.OpenRead(Path.Combine(root!, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json"));
        return JsonDocument.Parse(stream).RootElement.Clone();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void LineExtendModeAndChannelMode_HaveEveryEnumAndParameterKey(string code)
    {
        JsonElement locale = LoadLocale(code);
        var keys = Enum.GetValues<LineExtendMode>().Select(m => $"Enum_{nameof(LineExtendMode)}_{m}")
            .Concat(Enum.GetValues<ChannelMode>().Select(m => $"Enum_{nameof(ChannelMode)}_{m}"))
            .Append("Param_ChannelMode")
            .Append("Param_ExtendMode");

        foreach (string key in keys)
        {
            Assert.True(locale.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"[{code}] missing {key}");
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void WavePatternModeAndDrawingMeasureParameters_HaveEveryEnumAndParameterKey(string code)
    {
        JsonElement locale = LoadLocale(code);
        var keys = Enum.GetValues<WavePatternMode>().Select(m => $"Enum_{nameof(WavePatternMode)}_{m}")
            .Concat(Enum.GetValues<DrawingStrokeStyle>().Select(m => $"Enum_{nameof(DrawingStrokeStyle)}_{m}"))
            .Concat(new[]
            {
                "Param_StrokeStyle",
                "Param_ShowPriceLabel",
                "Param_ShowBarLabel",
                "Param_ShowVolumeLabel",
                "Param_ShowHighLowLabel",
                "Param_ShowDrawdownLabel",
                "Param_ShowVolatilityLabel",
                "Param_PatternMode",
                "Param_ShowRuleValidation"
            });

        foreach (string key in keys)
        {
            Assert.True(locale.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"[{code}] missing {key}");
        }
    }

    [Theory]
    [InlineData("en", "Extend Right", "Extend Left", "Extend Both")]
    [InlineData("ja", "\u53F3\u7AEF\u307E\u3067\u5EF6\u9577", "\u5DE6\u7AEF\u307E\u3067\u5EF6\u9577", "\u4E21\u7AEF\u307E\u3067\u5EF6\u9577")]
    public void AllDrawingExtendMenus_HaveUnifiedLabels_AcrossLinePivotAndFibRetracement(
        string code,
        string expectedRight,
        string expectedLeft,
        string expectedBoth)
    {
        JsonElement locale = LoadLocale(code);

        var requiredKeys = Enum.GetValues<LineExtendMode>().Select(m => $"Enum_{nameof(LineExtendMode)}_{m}")
            .Concat(Enum.GetValues<PivotLineExtendMode>().Select(m => $"Enum_{nameof(PivotLineExtendMode)}_{m}"))
            .Concat(Enum.GetValues<FibonacciRetracementExtendMode>().Select(m => $"Enum_{nameof(FibonacciRetracementExtendMode)}_{m}"))
            .Append("Setting_FibRetracement_ExtendMode")
            .Append("Param_ExtendMode")
            .Append("Param_ExtendLines");

        foreach (string key in requiredKeys)
        {
            Assert.True(locale.TryGetProperty(key, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString()), $"[{code}] missing {key}");
        }

        Assert.Equal(expectedRight, locale.GetProperty("Enum_LineExtendMode_Right").GetString());
        Assert.Equal(expectedLeft, locale.GetProperty("Enum_LineExtendMode_Left").GetString());
        Assert.Equal(expectedBoth, locale.GetProperty("Enum_LineExtendMode_Both").GetString());

        Assert.Equal(expectedRight, locale.GetProperty("Enum_PivotLineExtendMode_ExtendRight").GetString());
        Assert.Equal(expectedLeft, locale.GetProperty("Enum_PivotLineExtendMode_ExtendLeft").GetString());
        Assert.Equal(expectedBoth, locale.GetProperty("Enum_PivotLineExtendMode_ExtendBoth").GetString());

        Assert.Equal(expectedRight, locale.GetProperty("Enum_FibonacciRetracementExtendMode_ExtendRight").GetString());
        Assert.Equal(expectedLeft, locale.GetProperty("Enum_FibonacciRetracementExtendMode_ExtendLeft").GetString());
        Assert.Equal(expectedBoth, locale.GetProperty("Enum_FibonacciRetracementExtendMode_ExtendBoth").GetString());
    }
}
