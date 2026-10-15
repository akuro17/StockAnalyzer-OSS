using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class ArimaProjectionAnalysisTests
{
    private static List<CoreCandleData> WavyCandles(int count)
    {
        var candles = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal open = 100m + (i % 7) * 1.5m + i * 0.3m;
            decimal close = open + ((i % 5) - 2) * 1.25m;
            decimal high = Math.Max(open, close) + 1m + (i % 4) * 0.5m;
            decimal low = Math.Min(open, close) - 1m - (i % 3) * 0.25m;
            candles.Add(new CoreCandleData(new DateTime(2020, 1, 1).AddDays(i), open, high, low, close, 1000));
        }

        return candles;
    }

    // A Heikin-Ashi source on the raw candles must equal the matching plain field of the converter's Heikin-Ashi candles
    // (Open/High/Low/Close need no previous close), for a window that starts at the first bar and for one that does not.
    [Theory]
    [InlineData(PriceType.HeikinAshiOpen, PriceType.Open, 0)]
    [InlineData(PriceType.HeikinAshiHigh, PriceType.High, 0)]
    [InlineData(PriceType.HeikinAshiLow, PriceType.Low, 0)]
    [InlineData(PriceType.HeikinAshiClose, PriceType.Close, 0)]
    [InlineData(PriceType.HeikinAshiOpen, PriceType.Open, 15)]
    [InlineData(PriceType.HeikinAshiHigh, PriceType.High, 15)]
    [InlineData(PriceType.HeikinAshiLow, PriceType.Low, 15)]
    [InlineData(PriceType.HeikinAshiClose, PriceType.Close, 15)]
    public void HeikinAshiSource_EqualsPlainFieldOfConvertedCandles(PriceType haSource, PriceType plainField, int startIndex)
    {
        var raw = WavyCandles(70);
        var converted = HeikinAshiConverter.Convert(raw);
        int endIndex = raw.Count - 1;

        var actual = ArimaProjectionAnalysis.CalculateProjection(raw, startIndex, endIndex, priceSource: haSource);
        var expected = ArimaProjectionAnalysis.CalculateProjection(converted, startIndex, endIndex, priceSource: plainField);

        Assert.NotEqual(ArimaProjectionResult.Empty, actual);
        Assert.Equal(expected.ProjectedPoints, actual.ProjectedPoints);
        Assert.Equal(expected.UpperBandPoints, actual.UpperBandPoints);
        Assert.Equal(expected.LowerBandPoints, actual.LowerBandPoints);
        Assert.Equal(expected.TargetPrice, actual.TargetPrice);
    }

    [Theory]
    [InlineData(PriceType.Close)]
    [InlineData(PriceType.Median)]
    [InlineData(PriceType.TrueHigh)]
    [InlineData(PriceType.TrueLow)]
    public void NonHeikinAshiSource_StillProjects(PriceType source)
    {
        var raw = WavyCandles(70);

        var result = ArimaProjectionAnalysis.CalculateProjection(raw, 10, raw.Count - 1, priceSource: source);

        Assert.NotEqual(ArimaProjectionResult.Empty, result);
        Assert.Equal(raw.Count - 1 - 10 + 1, result.SampleCount);
        Assert.NotEmpty(result.ProjectedPoints);
    }
}
