using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Drawing;
using StockAnalyzer.Core.Drawing.Values;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Core.Tests.Drawing;

public sealed class DistributionRangeValuesTests
{
    private static readonly IndicatorColor Color = new(255, 40, 120, 220);

    [Fact]
    public void Calculate_WithNullCandles_ReturnsEmpty()
    {
        var p1 = new DrawingAnchor(DrawingValueTestCandles.First, 100m);
        var p2 = new DrawingAnchor(DrawingValueTestCandles.First.AddDays(20), 110m);

        var values = DistributionRangeValues.Calculate(p1, p2, candles: null, Color, new DrawingValueOptions());

        Assert.Empty(values);
    }

    [Fact]
    public void Calculate_ReturnsNineFixedValuesInEnUsFormat_InvariantToAnchorOrder()
    {
        var candles = DrawingValueTestCandles.Daily(60);
        var p1 = new DrawingAnchor(candles[5].Timestamp, candles[5].Close);
        var p2 = new DrawingAnchor(candles[45].Timestamp, candles[45].Close);

        var forward = DistributionRangeValues.Calculate(p1, p2, candles, Color, new DrawingValueOptions());
        var backward = DistributionRangeValues.Calculate(p2, p1, candles, Color, new DrawingValueOptions());

        Assert.Equal(DistributionRangeValues.ValueCount, forward.Count);
        Assert.Equal("SampleCount", forward[DistributionRangeValues.SampleCountIndex].Key);
        Assert.Equal("40", forward[DistributionRangeValues.SampleCountIndex].FormattedText);
        Assert.Equal("Mean", forward[DistributionRangeValues.MeanIndex].Key);
        Assert.Equal("StdDev", forward[DistributionRangeValues.StdDevIndex].Key);
        Assert.Equal("Median", forward[DistributionRangeValues.MedianIndex].Key);
        Assert.Equal("IQR", forward[DistributionRangeValues.IqrIndex].Key);
        Assert.Equal("Skewness", forward[DistributionRangeValues.SkewnessIndex].Key);
        Assert.Equal("ExcessKurtosis", forward[DistributionRangeValues.ExcessKurtosisIndex].Key);
        Assert.Equal("ReferenceDistribution", forward[DistributionRangeValues.ReferenceDistributionIndex].Key);
        Assert.Equal("BinCount", forward[DistributionRangeValues.BinCountIndex].Key);
        Assert.Equal(forward, backward);
    }

    [Fact]
    public void Calculate_RespectsPriceModeAndReferenceOptions()
    {
        var candles = DrawingValueTestCandles.Daily(40);
        var p1 = new DrawingAnchor(candles[0].Timestamp, candles[0].Close);
        var p2 = new DrawingAnchor(candles[^1].Timestamp, candles[^1].Close);
        var options = new DrawingValueOptions(
            DistributionDataMode: DistributionDataMode.Price,
            DistributionReferenceDistribution: ReferenceDistributionType.Pareto,
            DistributionBinningRule: DistributionBinningRule.FixedCount,
            DistributionFixedBinCount: 12);

        var values = DistributionRangeValues.Calculate(p1, p2, candles, Color, options);

        Assert.Equal(DistributionRangeValues.ValueCount, values.Count);
        Assert.Equal("40", values[DistributionRangeValues.SampleCountIndex].FormattedText);
        Assert.Equal("12", values[DistributionRangeValues.BinCountIndex].FormattedText);
        Assert.Contains("Pareto", values[DistributionRangeValues.ReferenceDistributionIndex].FormattedText);
    }
}
