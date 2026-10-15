using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Slice-only analyses (anchored VWAP, Time at Price) recurse Heikin-Ashi from the first supplied candle.
/// </summary>
public class HeikinAshiSliceConsumersTests
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
            candles.Add(new CoreCandleData(new DateTime(2020, 1, 1).AddDays(i), open, high, low, close, 1000 + i * 10));
        }

        return candles;
    }

    [Theory]
    [InlineData(PriceType.HeikinAshiOpen, PriceType.Open)]
    [InlineData(PriceType.HeikinAshiHigh, PriceType.High)]
    [InlineData(PriceType.HeikinAshiLow, PriceType.Low)]
    [InlineData(PriceType.HeikinAshiClose, PriceType.Close)]
    public void AnchoredVwap_HeikinAshiSource_EqualsPlainFieldOfConvertedCandles(PriceType haSource, PriceType plainField)
    {
        var raw = WavyCandles(60);
        var converted = HeikinAshiConverter.Convert(raw);

        var actual = VolumeAnalysis.CalculateAnchoredVwap(raw, haSource);
        var expected = VolumeAnalysis.CalculateAnchoredVwap(converted, plainField);

        Assert.Equal(raw.Count, actual.Count);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(PriceType.TrueHigh)]
    [InlineData(PriceType.TrueLow)]
    public void AnchoredVwap_TrueHighLow_UseThePreviousClose(PriceType source)
    {
        var raw = WavyCandles(40);
        var series = StockAnalyzer.Core.Models.Indicators.PriceDataHelper.ExtractPriceSeries(raw, source);

        var actual = VolumeAnalysis.CalculateAnchoredVwap(raw, source);

        decimal pv = 0m;
        long vol = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            pv += series[i]!.Value * raw[i].Volume;
            vol += raw[i].Volume;
            Assert.Equal(pv / vol, actual[i].Vwap);
        }
    }

    [Fact]
    public void AnchoredVwap_NonHeikinAshiSource_EqualsPerBarPrice()
    {
        var raw = WavyCandles(30);

        var actual = VolumeAnalysis.CalculateAnchoredVwap(raw, PriceType.Typical);

        decimal pv = 0m;
        long vol = 0;
        for (int i = 0; i < raw.Count; i++)
        {
            pv += (raw[i].High + raw[i].Low + raw[i].Close) / 3.0m * raw[i].Volume;
            vol += raw[i].Volume;
            Assert.Equal(pv / vol, actual[i].Vwap);
        }
    }

    [Theory]
    [InlineData(PriceType.HeikinAshiOpen)]
    [InlineData(PriceType.HeikinAshiHigh)]
    [InlineData(PriceType.HeikinAshiLow)]
    [InlineData(PriceType.HeikinAshiClose)]
    public void TimeAtPriceProfile_HeikinAshiSource_BinsTheRecursiveValues(PriceType haSource)
    {
        const int rows = 12;
        var raw = WavyCandles(60);
        var converted = HeikinAshiConverter.Convert(raw);

        decimal min = raw.Min(c => c.Low);
        decimal max = raw.Max(c => c.High);
        decimal binSize = (max - min) / rows;
        var expectedCounts = new long[rows];
        foreach (var ha in converted)
        {
            decimal price = haSource switch
            {
                PriceType.HeikinAshiOpen => ha.Open,
                PriceType.HeikinAshiHigh => ha.High,
                PriceType.HeikinAshiLow => ha.Low,
                _ => ha.Close
            };
            int idx = Math.Clamp((int)((price - min) / binSize), 0, rows - 1);
            expectedCounts[idx]++;
        }

        var bins = TimeAtPriceAnalysis.CalculateProfile(raw, rows, haSource);

        Assert.Equal(rows, bins.Count);
        Assert.Equal(expectedCounts, bins.Select(b => b.TotalVolume).ToArray());
    }
}
