using System;
using System.Text.Json;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Settings;
using StockAnalyzer.Core.Theme;
using Xunit;

namespace StockAnalyzer.Core.Tests.Models;

public class HeatmapColorMapperTests
{
    [Fact]
    public void Classify_Nan_ReturnsNoData()
    {
        var sample = HeatmapColorMapper.Classify(double.NaN, 0.05, 10);
        Assert.Equal(HeatmapColorKind.NoData, sample.Kind);
        Assert.Equal(0.0, sample.Factor);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.00004)]
    [InlineData(-0.00004)]
    [InlineData(0.00004999)]
    [InlineData(-0.00004999)]
    public void Classify_ZeroBand_ReturnsNeutral(double ret)
    {
        var sample = HeatmapColorMapper.Classify(ret, 0.05, 10);
        Assert.Equal(HeatmapColorKind.Neutral, sample.Kind);
        Assert.Equal(0.0, sample.Factor);
    }

    [Theory]
    [InlineData(0.00005, HeatmapColorKind.Up)]
    [InlineData(-0.00005, HeatmapColorKind.Down)]
    public void Classify_NeutralBandBoundary_IsNotNeutral(double ret, HeatmapColorKind expectedKind)
    {
        var sample = HeatmapColorMapper.Classify(ret, 0.05, 10);
        Assert.Equal(expectedKind, sample.Kind);
        Assert.Equal(0.0, sample.Factor);
    }

    [Theory]
    [InlineData(0.005, 1, 0.0)]
    [InlineData(0.0050001, 2, 1.0 / 9.0)]
    [InlineData(0.01, 2, 1.0 / 9.0)]
    [InlineData(0.0249999, 5, 4.0 / 9.0)]
    [InlineData(0.025, 5, 4.0 / 9.0)]
    [InlineData(0.05, 10, 1.0)]
    [InlineData(0.0500001, 10, 1.0)]
    [InlineData(0.2, 10, 1.0)]
    [InlineData(double.PositiveInfinity, 10, 1.0)]
    public void Classify_SteppedLevels_T05_N10_Positive(double ret, int expectedLevel, double expectedFactor)
    {
        var sample = HeatmapColorMapper.Classify(ret, 0.05, 10);
        Assert.Equal(HeatmapColorKind.Up, sample.Kind);
        Assert.Equal(expectedFactor, sample.Factor, precision: 6);
    }

    [Theory]
    [InlineData(-0.005, 1, 0.0)]
    [InlineData(-0.0050001, 2, 1.0 / 9.0)]
    [InlineData(-0.01, 2, 1.0 / 9.0)]
    [InlineData(-0.0249999, 5, 4.0 / 9.0)]
    [InlineData(-0.025, 5, 4.0 / 9.0)]
    [InlineData(-0.05, 10, 1.0)]
    [InlineData(-0.0500001, 10, 1.0)]
    [InlineData(-0.2, 10, 1.0)]
    [InlineData(double.NegativeInfinity, 10, 1.0)]
    public void Classify_SteppedLevels_T05_N10_NegativeMirror(double ret, int expectedLevel, double expectedFactor)
    {
        var sample = HeatmapColorMapper.Classify(ret, 0.05, 10);
        Assert.Equal(HeatmapColorKind.Down, sample.Kind);
        Assert.Equal(expectedFactor, sample.Factor, precision: 6);
    }

    [Fact]
    public void Classify_ContinuousMode_N0_ReturnsExactR()
    {
        var sample = HeatmapColorMapper.Classify(0.025, 0.05, 0);
        Assert.Equal(HeatmapColorKind.Up, sample.Kind);
        Assert.Equal(0.5, sample.Factor, precision: 6);

        var sampleNeg = HeatmapColorMapper.Classify(-0.01, 0.05, 0);
        Assert.Equal(HeatmapColorKind.Down, sampleNeg.Kind);
        Assert.Equal(0.2, sampleNeg.Factor, precision: 6);
    }

    [Fact]
    public void Classify_N1_ReturnsFactor0ForAnyNonNeutral()
    {
        var sample = HeatmapColorMapper.Classify(0.0001, 0.05, 1);
        Assert.Equal(HeatmapColorKind.Up, sample.Kind);
        Assert.Equal(0.0, sample.Factor);

        var sampleNeg = HeatmapColorMapper.Classify(-0.04, 0.05, 1);
        Assert.Equal(HeatmapColorKind.Down, sampleNeg.Kind);
        Assert.Equal(0.0, sampleNeg.Factor);
    }

    [Theory]
    [InlineData(5.0, 1.0, 0.05)]
    [InlineData(5.0, 2.0, 0.10)]
    [InlineData(5.0, 0.1, 0.005)]
    [InlineData(0.1, 0.1, 0.001)] // min clamp
    [InlineData(100.0, 10.0, 1.0)] // max clamp
    [InlineData(double.NaN, 1.0, 0.05)]
    [InlineData(5.0, double.NaN, 0.05)]
    public void ResolveThresholdRatio_CalculatesAndClamps(double thresholdPercent, double multiplier, double expected)
    {
        double actual = HeatmapColorMapper.ResolveThresholdRatio(thresholdPercent, multiplier);
        Assert.Equal(expected, actual, precision: 6);
    }

    [Fact]
    public void Classify_StepMonotonicity_And_Symmetry()
    {
        double prevFactor = 0.0;
        for (double r = 0.0001; r <= 0.10; r += 0.001)
        {
            var up = HeatmapColorMapper.Classify(r, 0.05, 10);
            var down = HeatmapColorMapper.Classify(-r, 0.05, 10);

            Assert.Equal(HeatmapColorKind.Up, up.Kind);
            Assert.Equal(HeatmapColorKind.Down, down.Kind);
            Assert.Equal(up.Factor, down.Factor);

            Assert.True(up.Factor >= prevFactor, $"Factor must be non-decreasing: r={r}, current={up.Factor}, prev={prevFactor}");
            prevFactor = up.Factor;
        }
    }

    [Fact]
    public void DefaultColors_SurviveHsvDataRoundTrip()
    {
        string up = ChartSettingsConstants.DefaultScatterHeatmapUpColor;
        string neutral = ChartSettingsConstants.DefaultScatterHeatmapNeutralColor;
        string down = ChartSettingsConstants.DefaultScatterHeatmapDownColor;

        Assert.Equal(up, HsvData.FromHtmlSafe(up).ToHtml());
        Assert.Equal(neutral, HsvData.FromHtmlSafe(neutral).ToHtml());
        Assert.Equal(down, HsvData.FromHtmlSafe(down).ToHtml());
    }

    [Fact]
    public void GlobalChartSettings_ScatterHeatmap_DefaultsMatchConstants()
    {
        var settings = new GlobalChartSettings();
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapUpColor, settings.ScatterHeatmapUpColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapNeutralColor, settings.ScatterHeatmapNeutralColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapDownColor, settings.ScatterHeatmapDownColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapThresholdPercent, settings.ScatterHeatmapThresholdPercent);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapMultiplier, settings.ScatterHeatmapMultiplier);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapStepCount, settings.ScatterHeatmapStepCount);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapFontSize, settings.ScatterHeatmapFontSize);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapTextColor, settings.ScatterHeatmapTextColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapTextAlignment, settings.ScatterHeatmapTextAlignment);
    }

    [Fact]
    public void GlobalChartSettings_ScatterHeatmap_ValidateClampsAndRecovers()
    {
        var invalid = new GlobalChartSettings
        {
            ScatterHeatmapUpColor = "not-a-color",
            ScatterHeatmapNeutralColor = "#ZZZZZZ",
            ScatterHeatmapDownColor = null!,
            ScatterHeatmapThresholdPercent = -10.0,
            ScatterHeatmapMultiplier = double.NaN,
            ScatterHeatmapStepCount = 999,
            ScatterHeatmapFontSize = -5.0,
            ScatterHeatmapTextColor = "invalid",
            ScatterHeatmapTextAlignment = (HeatmapTextAlignment)999
        };

        var valid = invalid.Validate();

        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapUpColor, valid.ScatterHeatmapUpColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapNeutralColor, valid.ScatterHeatmapNeutralColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapDownColor, valid.ScatterHeatmapDownColor);
        Assert.Equal(ChartSettingsConstants.MinScatterHeatmapThresholdPercent, valid.ScatterHeatmapThresholdPercent);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapMultiplier, valid.ScatterHeatmapMultiplier);
        Assert.Equal(ChartSettingsConstants.MaxScatterHeatmapStepCount, valid.ScatterHeatmapStepCount);
        Assert.Equal(ChartSettingsConstants.MinScatterHeatmapFontSize, valid.ScatterHeatmapFontSize);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapTextColor, valid.ScatterHeatmapTextColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapTextAlignment, valid.ScatterHeatmapTextAlignment);
    }

    [Fact]
    public void GlobalChartSettings_ScatterHeatmap_JsonRoundTrip_PreservesValues()
    {
        var settings = new GlobalChartSettings
        {
            ScatterHeatmapUpColor = "#FF123456",
            ScatterHeatmapNeutralColor = "#FF654321",
            ScatterHeatmapDownColor = "#FFABCDEF",
            ScatterHeatmapThresholdPercent = 8.5,
            ScatterHeatmapMultiplier = 1.5,
            ScatterHeatmapStepCount = 20,
            ScatterHeatmapFontSize = 18.0,
            ScatterHeatmapTextColor = "#FFEEEEEE",
            ScatterHeatmapTextAlignment = HeatmapTextAlignment.Right
        };

        var json = JsonSerializer.Serialize(settings, GlobalChartSettingsJsonContext.Default.GlobalChartSettings);
        var restored = JsonSerializer.Deserialize(json, GlobalChartSettingsJsonContext.Default.GlobalChartSettings);

        Assert.NotNull(restored);
        Assert.Equal("#FF123456", restored!.ScatterHeatmapUpColor);
        Assert.Equal("#FF654321", restored.ScatterHeatmapNeutralColor);
        Assert.Equal("#FFABCDEF", restored.ScatterHeatmapDownColor);
        Assert.Equal(8.5, restored.ScatterHeatmapThresholdPercent);
        Assert.Equal(1.5, restored.ScatterHeatmapMultiplier);
        Assert.Equal(20u, restored.ScatterHeatmapStepCount);
        Assert.Equal(18.0, restored.ScatterHeatmapFontSize);
        Assert.Equal("#FFEEEEEE", restored.ScatterHeatmapTextColor);
        Assert.Equal(HeatmapTextAlignment.Right, restored.ScatterHeatmapTextAlignment);
    }

    [Fact]
    public void GlobalChartSettings_OlderJsonWithoutKeys_DeserializesWithDefaults()
    {
        string emptyJson = "{}";
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var deserialized = JsonSerializer.Deserialize<GlobalChartSettings>(emptyJson, options);

        Assert.NotNull(deserialized);
        var validated = deserialized!.Validate();
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapUpColor, validated.ScatterHeatmapUpColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapNeutralColor, validated.ScatterHeatmapNeutralColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapDownColor, validated.ScatterHeatmapDownColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapThresholdPercent, validated.ScatterHeatmapThresholdPercent);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapMultiplier, validated.ScatterHeatmapMultiplier);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapStepCount, validated.ScatterHeatmapStepCount);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapFontSize, validated.ScatterHeatmapFontSize);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapTextColor, validated.ScatterHeatmapTextColor);
        Assert.Equal(ChartSettingsConstants.DefaultScatterHeatmapTextAlignment, validated.ScatterHeatmapTextAlignment);
    }
}
