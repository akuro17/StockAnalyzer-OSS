using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

public class FrechetProjectionHeikinAshiTests
{
    private static List<CoreCandleData> WavyCandles(int count)
    {
        var candles = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal open = 100m + (i % 9) * 1.7m + (i % 4) * 0.6m;
            decimal close = open + ((i % 5) - 2) * 1.25m;
            decimal high = Math.Max(open, close) + 1m + (i % 4) * 0.5m;
            decimal low = Math.Min(open, close) - 1m - (i % 3) * 0.25m;
            candles.Add(new CoreCandleData(new DateTime(2020, 1, 1).AddDays(i), open, high, low, close, 1000));
        }

        return candles;
    }

    // A Heikin-Ashi source on the raw candles must equal the matching plain field of the converter's Heikin-Ashi candles.
    [Theory]
    [InlineData(PriceType.HeikinAshiOpen, PriceType.Open)]
    [InlineData(PriceType.HeikinAshiHigh, PriceType.High)]
    [InlineData(PriceType.HeikinAshiLow, PriceType.Low)]
    [InlineData(PriceType.HeikinAshiClose, PriceType.Close)]
    public void HeikinAshiSource_EqualsPlainFieldOfConvertedCandles(PriceType haSource, PriceType plainField)
    {
        var raw = WavyCandles(120);
        var converted = HeikinAshiConverter.Convert(raw);
        int queryEnd = raw.Count - 1;
        int queryStart = queryEnd - 11;

        var actual = FrechetDistanceAnalysis.CalculateProjection(raw, queryStart, queryEnd, horizon: 5, priceType: haSource);
        var expected = FrechetDistanceAnalysis.CalculateProjection(converted, queryStart, queryEnd, horizon: 5, priceType: plainField);

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.MatchedStartIndex, actual!.MatchedStartIndex);
        Assert.Equal(expected.MatchedEndIndex, actual.MatchedEndIndex);
        Assert.Equal(expected.Distance, actual.Distance);
        Assert.Equal(expected.Projections, actual.Projections);
    }

    // True High / True Low use the previous bar's close: equal to the plain Close of candles whose Close is that series value.
    [Theory]
    [InlineData(PriceType.TrueHigh)]
    [InlineData(PriceType.TrueLow)]
    public void TrueHighLow_UseThePreviousClose(PriceType source)
    {
        var raw = WavyCandles(120);
        var series = StockAnalyzer.Core.Models.Indicators.PriceDataHelper.ExtractPriceSeries(raw, source);
        var replaced = new List<CoreCandleData>();
        for (int i = 0; i < raw.Count; i++)
        {
            replaced.Add(raw[i] with { Close = series[i]!.Value });
        }

        int queryEnd = raw.Count - 1;
        var actual = FrechetDistanceAnalysis.CalculateProjection(raw, queryEnd - 11, queryEnd, horizon: 5, priceType: source);
        var expected = FrechetDistanceAnalysis.CalculateProjection(replaced, queryEnd - 11, queryEnd, horizon: 5, priceType: PriceType.Close);

        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.MatchedStartIndex, actual!.MatchedStartIndex);
        Assert.Equal(expected.Distance, actual.Distance);
        Assert.Equal(expected.Projections, actual.Projections);
    }

    [Theory]
    [InlineData(PriceType.Close)]
    [InlineData(PriceType.Median)]
    [InlineData(PriceType.TrueHigh)]
    public void NonHeikinAshiSource_StillMatches(PriceType source)
    {
        var raw = WavyCandles(120);
        int queryEnd = raw.Count - 1;

        var result = FrechetDistanceAnalysis.CalculateProjection(raw, queryEnd - 11, queryEnd, horizon: 5, priceType: source);

        Assert.NotNull(result);
        Assert.NotEmpty(result!.Projections);
    }
}
