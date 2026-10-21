#nullable enable
using System.Linq;
using System.Text.Json;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Analysis;
using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Screener;
using StockAnalyzer.Core.Services;
using StockAnalyzer.Core.Tests.TestHelpers;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class ScatterMetricDefinitionTests
{
    [Fact]
    public void Label_WithoutOffset_IsTheScreenerColumnTitle()
    {
        var metric = ScatterMetricTestKit.Indicator(IndicatorType.RSI, offset: 0, output: null, ("Period", 14));

        Assert.Equal(metric.Source.GetColumnHeaderTitle(), metric.Label);
        Assert.DoesNotContain("[-", metric.Label);
    }

    [Fact]
    public void Label_WithOffset_AppendsBarsBack()
    {
        var metric = ScatterMetricTestKit.Indicator(IndicatorType.RSI, offset: 3, output: null, ("Period", 14));

        Assert.Equal(metric.Source.GetColumnHeaderTitle() + " [-3]", metric.Label);
    }

    private static ScatterMetricDefinition ColumnClose(int offset = 0)
        => ScatterMetricTestKit.Column(ScatterMetricTestKit.CatalogColumn("Close"), offset);

    [Fact]
    public void Label_OnDay_IsUnchanged_ForAnIndicatorAColumnAndAPriceSource()
    {
        var rsi = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Period", 14));
        var close = ColumnClose();
        var price = ScatterMetricTestKit.Indicator(IndicatorType.Price, 0, "Close");
        price.Source.CustomDisplayName = "Close";

        Assert.Equal(rsi.Source.GetColumnHeaderTitle(), rsi.Label);
        Assert.Equal(close.Source.CustomDisplayName, close.Label);
        Assert.Equal("Close", price.Label);
        Assert.DoesNotContain("[", close.Label);
    }

    [Fact]
    public void Label_OfAnIndicatorOnWeekOrMonth_NamesTheTimeFrameOnce_BeforeTheOffset()
    {
        var weekly = ScatterMetricTestKit.OnFrame(ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Period", 14)), TimeFrame.W1);
        var weeklyBack = ScatterMetricTestKit.OnFrame(ScatterMetricTestKit.Indicator(IndicatorType.RSI, 2, null, ("Period", 14)), TimeFrame.W1);
        var monthly = ScatterMetricTestKit.OnFrame(ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Period", 14)), TimeFrame.MN1);

        // The Screener title already carries the time frame of an Indicator; the Label must not add it a second time.
        Assert.Equal(weekly.Source.GetColumnHeaderTitle(), weekly.Label);
        Assert.EndsWith(" [W1]", weekly.Label);
        Assert.Equal(weekly.Source.GetColumnHeaderTitle() + " [-2]", weeklyBack.Label);
        Assert.EndsWith(" [W1] [-2]", weeklyBack.Label);
        Assert.EndsWith(" [MN1]", monthly.Label);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(weeklyBack.Label, @"\[W1\]"));
    }

    [Fact]
    public void Label_OfAColumnOnWeekOrMonth_AddsTheTimeFrame_BeforeTheOffset()
    {
        string title = ColumnClose().Source.CustomDisplayName;

        Assert.Equal(title + " [MN1]", ScatterMetricTestKit.OnFrame(ColumnClose(), TimeFrame.MN1).Label);
        Assert.Equal(title + " [W1] [-1]", ScatterMetricTestKit.OnFrame(ColumnClose(offset: 1), TimeFrame.W1).Label);
        Assert.Equal(title + " [-1]", ColumnClose(offset: 1).Label);
    }

    [Fact]
    public void Label_OfAPriceSourceOnWeek_AddsTheTimeFrame_AsThePriceTitleIsItsCatalogDescription()
    {
        // The registration screen names a Price item by its catalog description (CustomDisplayName), exactly as it names a Column.
        var price = ScatterMetricTestKit.Indicator(IndicatorType.Price, 3, "Close");
        price.Source.CustomDisplayName = "Close";
        price.Source.TimeFrame = TimeFrame.W1;

        Assert.Equal("Close [W1] [-3]", price.Label);
    }

    [Fact]
    public void ContentKey_DiffersByTimeFrame()
    {
        var day = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Period", 14));

        Assert.NotEqual(day.ContentKey, ScatterMetricTestKit.OnFrame(day.Clone(), TimeFrame.W1).ContentKey);
        Assert.NotEqual(ScatterMetricTestKit.OnFrame(day.Clone(), TimeFrame.W1).ContentKey, ScatterMetricTestKit.OnFrame(day.Clone(), TimeFrame.MN1).ContentKey);
    }

    [Theory]
    [InlineData(TimeFrame.W1)]
    [InlineData(TimeFrame.MN1)]
    public void ATimeFrame_SurvivesAJsonRoundTrip_WithTheSameContentKeyAndLabel(TimeFrame frame)
    {
        var metric = ScatterMetricTestKit.OnFrame(ScatterMetricTestKit.Indicator(IndicatorType.RSI, 1, null, ("Period", 14)), frame);
        var column = ScatterMetricTestKit.OnFrame(ColumnClose(offset: 1), frame);

        foreach (ScatterMetricDefinition original in new[] { metric, column })
        {
            var restored = JsonSerializer.Deserialize<ScatterMetricDefinition>(JsonSerializer.Serialize(original))!;

            Assert.Equal(frame, restored.Source.TimeFrame);
            Assert.Equal(original.ContentKey, restored.ContentKey);
            Assert.Equal(original.Label, restored.Label);
        }
    }

    [Fact]
    public void ContentKey_IgnoresIdAndParameterOrder()
    {
        var first = ScatterMetricTestKit.Indicator(IndicatorType.MACD, 0, null, ("FastPeriod", 12), ("SlowPeriod", 26));
        var second = ScatterMetricTestKit.Indicator(IndicatorType.MACD, 0, null, ("SlowPeriod", 26), ("FastPeriod", 12));

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ContentKey, second.ContentKey);
    }

    [Fact]
    public void ContentKey_DiffersWhenAnyContentPartDiffers()
    {
        var baseline = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Period", 14));

        Assert.NotEqual(baseline.ContentKey, ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Period", 21)).ContentKey);
        Assert.NotEqual(baseline.ContentKey, ScatterMetricTestKit.Indicator(IndicatorType.RSI, 1, null, ("Period", 14)).ContentKey);
        Assert.NotEqual(baseline.ContentKey, ScatterMetricTestKit.Indicator(IndicatorType.SMA, 0, null, ("Period", 14)).ContentKey);
    }

    [Fact]
    public void ContentKey_KeepsTheDigitsOfADecimalAndOfA64BitInteger()
    {
        // Two such values share one double; they are different values, so an edit between them is a change.
        var tenth = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Scale", 0.1m));
        var nearTenth = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Scale", 0.1000000000000000055511151231m));
        var big = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Count", 9007199254740993L));
        var bigNeighbour = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Count", 9007199254740992L));

        Assert.NotEqual(tenth.ContentKey, nearTenth.ContentKey);
        Assert.NotEqual(big.ContentKey, bigNeighbour.ContentKey);
    }

    [Fact]
    public void ContentKey_TreatsTrailingZerosOfADecimalAsTheSameValue()
    {
        var plain = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Scale", 2.5m));
        var padded = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Scale", 2.50m));

        Assert.Equal(plain.ContentKey, padded.ContentKey);
    }

    [Fact]
    public void ContentKey_IsStableAcrossAJsonRoundTrip()
    {
        var metric = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 2, null,
            ("Period", 14), ("Ratio", 2.5), ("Scale", 0.1m), ("Smooth", true), ("Mode", "wilder"), ("Source", PriceType.Close));

        string json = JsonSerializer.Serialize(metric);
        var restored = JsonSerializer.Deserialize<ScatterMetricDefinition>(json)!;

        // The key is normalised, so it survives without typing the values again; the label needs the normaliser (ScatterMetricParameterRoundTripTests).
        Assert.Equal(metric.ContentKey, restored.ContentKey);
        Assert.Equal(metric.Id, restored.Id);
    }

    [Fact]
    public void Clone_IsIndependentAndKeepsTheId()
    {
        var metric = ScatterMetricTestKit.Indicator(IndicatorType.RSI, 0, null, ("Period", 14));
        var copy = metric.Clone();

        copy.Source.Parameters["Period"] = 99;
        copy.Source.Offset = 7;

        Assert.Equal(metric.Id, copy.Id);
        Assert.Equal(14, metric.Source.Parameters["Period"]);
        Assert.Equal(0, metric.Source.Offset);
    }

    [Fact]
    public void DatasetFixedRanges_DefaultToNull()
    {
        var dataset = new ScatterDataset("t", "x", "y", System.Array.Empty<ScatterPoint>());

        Assert.Null(dataset.XFixedRange);
        Assert.Null(dataset.YFixedRange);
    }
}
