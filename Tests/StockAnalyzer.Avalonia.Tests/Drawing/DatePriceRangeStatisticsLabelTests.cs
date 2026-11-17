using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Serialization;
using StockAnalyzer.Avalonia.Services;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Plan 07/08 X-1 (docs/sa_implementation_plan_WebAiPeerReview_Plans07to08, Run 5): the three optional statistics rows of the Date/Price Range
/// summary box (High/Low, MaxDD, Volatility), their settings, localization, save compatibility and Data Window rows.
/// </summary>
[Collection("DrawingThemeContext State")]
public sealed class DatePriceRangeStatisticsLabelTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters =
        {
            new ChartObjectJsonConverter(),
            new AvaloniaColorJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    private static readonly string[] ToggleNames = { "ShowHighLowLabel", "ShowDrawdownLabel", "ShowVolatilityLabel" };

    private static DatePriceRangeObject CreateRange()
        => new(PixelCoordinateTransform.Chart(100, 200), PixelCoordinateTransform.Chart(200, 100));

    /// <summary>Three bars at the start, the middle and the end of the range with the given closes.</summary>
    private static CoreCandleData[] ThreeBars(DatePriceRangeObject range, decimal c1, decimal c2, decimal c3)
    {
        DateTime tMin = range.Points[0].Time <= range.Points[1].Time ? range.Points[0].Time : range.Points[1].Time;
        DateTime tMax = range.Points[0].Time <= range.Points[1].Time ? range.Points[1].Time : range.Points[0].Time;
        return new[]
        {
            new CoreCandleData(tMin, c1, c1 + 5m, c1 - 5m, c1, 1_000L),
            new CoreCandleData(tMin + ((tMax - tMin) / 2), c2, c2 + 5m, c2 - 5m, c2, 2_000L),
            new CoreCandleData(tMax, c3, c3 + 5m, c3 - 5m, c3, 3_000L)
        };
    }

    /// <summary>The rows the summary box currently shows (the object rebuilds a stale cache first, exactly as before a render).</summary>
    private static string[] LabelLines(DatePriceRangeObject range) => range.GetLabelLines().ToArray();

    private static void SetToggles(DatePriceRangeObject range, bool value)
    {
        range.ShowHighLowLabel = value;
        range.ShowDrawdownLabel = value;
        range.ShowVolatilityLabel = value;
    }

    // ---- default look unchanged ----

    [Fact]
    public void NewToggles_DefaultToFalse_AndTheDefaultSummaryBoxKeepsItsThreeRows()
    {
        using var range = CreateRange();
        Assert.False(range.ShowHighLowLabel);
        Assert.False(range.ShowDrawdownLabel);
        Assert.False(range.ShowVolatilityLabel);

        range.Recalculate(ThreeBars(range, 105m, 112m, 118m));
        var lines = LabelLines(range);

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Vol: ", lines[2]);
    }

    // ---- row texts ----

    [Fact]
    public void ToggledOn_AppendsTheThreeRowsBelowTheExistingOnes_InOrder()
    {
        using var range = CreateRange();
        range.Recalculate(ThreeBars(range, 105m, 112m, 118m));
        SetToggles(range, true);

        var lines = LabelLines(range);

        Assert.Equal(6, lines.Length);
        Assert.Equal("High/Low: 123.000 / 100.000", lines[3]); // highs 110/117/123, lows 100/107/113
        Assert.Equal("MaxDD: 0.00%", lines[4]);
        Assert.Equal("Volatility: 0.87%/bar", lines[5]); // returns ln(112/105), ln(118/112): sample stdev 0.00874
    }

    [Fact]
    public void ToggledOn_ShowsThePeakToTroughFallAsAPositivePercentage()
    {
        using var range = CreateRange();
        range.Recalculate(ThreeBars(range, 100m, 120m, 90m));
        range.ShowDrawdownLabel = true;

        var lines = LabelLines(range);

        Assert.Equal(4, lines.Length);
        Assert.Equal("MaxDD: 25.00%", lines[3]); // peak 120, then 90
    }

    [Fact]
    public void EachToggleAddsOnlyItsOwnRow()
    {
        using var range = CreateRange();
        range.Recalculate(ThreeBars(range, 105m, 112m, 118m));

        range.ShowVolatilityLabel = true;
        var lines = LabelLines(range);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("Volatility: ", lines[3]);

        range.ShowVolatilityLabel = false;
        range.ShowHighLowLabel = true;
        lines = LabelLines(range);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("High/Low: ", lines[3]);
    }

    [Fact]
    public void WhenAValueIsUndefined_TheRowStaysAndShowsADash()
    {
        // No bar inside the range: every statistic is undefined.
        using var range = CreateRange();
        range.Recalculate(Array.Empty<CoreCandleData>());
        SetToggles(range, true);

        var lines = LabelLines(range);

        Assert.Equal(6, lines.Length);
        Assert.Equal("High/Low: - / -", lines[3]);
        Assert.Equal("MaxDD: -", lines[4]);
        Assert.Equal("Volatility: -", lines[5]);

        // Two bars: prices and drawdown exist, the volatility needs three bars.
        var twoBars = ThreeBars(range, 100m, 110m, 120m).Take(2).ToArray();
        range.Recalculate(twoBars);
        lines = LabelLines(range);
        Assert.Equal("High/Low: 115.000 / 95.000", lines[3]);
        Assert.Equal("MaxDD: 0.00%", lines[4]);
        Assert.Equal("Volatility: -", lines[5]);
    }

    [Fact]
    public void RowsFollowTheFixedCultureNotTheThreadCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            using var range = CreateRange();
            range.Recalculate(ThreeBars(range, 105m, 112m, 118m));
            SetToggles(range, true);

            var lines = LabelLines(range);

            Assert.Equal("High/Low: 123.000 / 100.000", lines[3]);
            Assert.Equal("Volatility: 0.87%/bar", lines[5]);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ToggleChangeAfterTheCalculation_IsPickedUpWithoutANewCalculation()
    {
        using var range = CreateRange();
        range.Recalculate(ThreeBars(range, 105m, 112m, 118m));
        Assert.Equal(3, LabelLines(range).Length);

        range.ShowHighLowLabel = true;
        Assert.Equal(4, LabelLines(range).Length);
        range.ShowHighLowLabel = false;
        Assert.Equal(3, LabelLines(range).Length);
    }

    // ---- Data Window ----

    [Fact]
    public void DataWindow_ListsAllTenValues_WhateverTheToggles()
    {
        using var range = CreateRange();
        var bars = ThreeBars(range, 105m, 112m, 118m);
        range.Recalculate(bars);

        var values = range.GetCalculatedValues(bars[0].Timestamp);

        Assert.Equal(DatePriceRangeValues.ValueCount, values.Count);
        Assert.Equal(
            new[] { "PriceDelta", "PriceChangePercent", "BarCount", "CalendarDays", "TotalVolume", "AverageVolume", "HighestHigh", "LowestLow", "MaxDrawdown", "Volatility" },
            values.Select(v => v.Key).ToArray());
        Assert.Equal(
            new[] { "Highest High", "Lowest Low", "Max Drawdown %", "Volatility %/bar" },
            values.Skip(DatePriceRangeValues.BaseValueCount).Select(v => v.Label).ToArray());
        Assert.Equal("123.000", values[DatePriceRangeValues.HighestHighIndex].FormattedText);
        Assert.Equal("100.000", values[DatePriceRangeValues.LowestLowIndex].FormattedText);
        Assert.Equal("0.00%", values[DatePriceRangeValues.MaxDrawdownIndex].FormattedText);
        Assert.EndsWith("%", values[DatePriceRangeValues.VolatilityIndex].FormattedText);
        Assert.DoesNotContain("/bar", values[DatePriceRangeValues.VolatilityIndex].FormattedText);
    }

    // ---- save / load / copy ----

    [Fact]
    public void Serialization_RoundTripsTheThreeNewToggles()
    {
        using var original = CreateRange();
        original.ShowHighLowLabel = true;
        original.ShowDrawdownLabel = false;
        original.ShowVolatilityLabel = true;

        string json = JsonSerializer.Serialize<IChartObject>(original, JsonOptions);
        using var restored = Assert.IsType<DatePriceRangeObject>(JsonSerializer.Deserialize<IChartObject>(json, JsonOptions));

        Assert.True(restored.ShowHighLowLabel);
        Assert.False(restored.ShowDrawdownLabel);
        Assert.True(restored.ShowVolatilityLabel);
    }

    [Fact]
    public void LoadingASavedRangeWithoutTheNewKeys_LeavesThemOff_AndKeepsTheOldToggles()
    {
        using var original = CreateRange();
        original.ShowVolumeLabel = false;
        string json = JsonSerializer.Serialize<IChartObject>(original, JsonOptions);

        var node = JsonNode.Parse(json)!.AsObject();
        int removed = 0;
        foreach (var key in node.Select(kv => kv.Key).Where(k => ToggleNames.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            node.Remove(key);
            removed++;
        }

        Assert.Equal(3, removed); // the new keys really are written, so removing them models a file saved before this change
        using var restored = Assert.IsType<DatePriceRangeObject>(JsonSerializer.Deserialize<IChartObject>(node.ToJsonString(), JsonOptions));

        Assert.False(restored.ShowHighLowLabel);
        Assert.False(restored.ShowDrawdownLabel);
        Assert.False(restored.ShowVolatilityLabel);
        Assert.True(restored.ShowPriceLabel);
        Assert.True(restored.ShowBarLabel);
        Assert.False(restored.ShowVolumeLabel);
    }

    [Fact]
    public void DuplicatedRange_KeepsTheNewToggles()
    {
        var manager = new ChartObjectManager();
        using var range = CreateRange();
        range.ShowHighLowLabel = true;
        range.ShowVolatilityLabel = true;
        manager.AddObject(range);

        var clone = Assert.IsType<DatePriceRangeObject>(Assert.Single(manager.DuplicateObjects(new[] { range.Id })));

        Assert.True(clone.ShowHighLowLabel);
        Assert.False(clone.ShowDrawdownLabel);
        Assert.True(clone.ShowVolatilityLabel);
    }

    // ---- settings screen ----

    [Fact]
    public void SettingsOrder_AndAttributes_OfTheNewToggles_FollowShowVolumeLabel()
    {
        var metadata = DrawingParameterViewBuilder.GetOrReflectMetadata(typeof(DatePriceRangeObject));
        var analysis = metadata.Where(m => m.Category == "Analysis").Select(m => m.Prop.Name).ToList();

        Assert.Equal(
            new[] { "ShowPriceLabel", "ShowBarLabel", "ShowVolumeLabel", "ShowHighLowLabel", "ShowDrawdownLabel", "ShowVolatilityLabel" },
            analysis.ToArray());

        var reference = metadata.Single(m => m.Prop.Name == "ShowVolumeLabel");
        foreach (string name in ToggleNames)
        {
            var item = metadata.Single(m => m.Prop.Name == name);
            Assert.Equal(typeof(bool), item.Prop.PropertyType);
            Assert.Equal(reference.Category, item.Category);
            Assert.Equal(reference.Order, item.Order);
            Assert.Equal(reference.Tags, item.Tags);
            Assert.NotNull(item.Display);
        }
    }

    [Fact]
    public void PresetObjectOfTheTool_ExposesTheNewTogglesOff()
    {
        var preset = Assert.IsType<DatePriceRangeObject>(DrawingToolPresetService.Shared.GetOrCreatePresetObject(DrawingTool.DatePriceRange));

        Assert.False(preset.ShowHighLowLabel);
        Assert.False(preset.ShowDrawdownLabel);
        Assert.False(preset.ShowVolatilityLabel);
    }

    // ---- localization ----

    [Theory]
    [InlineData("en")]
    [InlineData("ja")]
    public void LocaleFile_HasTheThreeKeys_RightAfterParamShowVolumeLabel_InOrder(string code)
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json")))
        {
            root = Path.GetDirectoryName(root)!;
        }

        Assert.NotNull(root);
        string path = Path.Combine(root!, "StockAnalyzer.Avalonia", "Resources", "Locales", $"{code}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();

        int volume = keys.IndexOf("Param_ShowVolumeLabel");
        Assert.True(volume >= 0);
        Assert.Equal("Param_ShowHighLowLabel", keys[volume + 1]);
        Assert.Equal("Param_ShowDrawdownLabel", keys[volume + 2]);
        Assert.Equal("Param_ShowVolatilityLabel", keys[volume + 3]);
        foreach (string key in keys.Skip(volume + 1).Take(3))
        {
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty(key).GetString()));
        }
    }
}
