using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Utilities;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// A Heikin-Ashi price source on the raw candles must give what the matching plain field gives on the converter's
/// Heikin-Ashi candles (recursion from the first candle, equal to the chart), for the drawing tools that read a price source.
/// Timestamps are preserved by the converter, so both objects use the same anchor times.
/// </summary>
public class HeikinAshiPriceSourceTests
{
    private static List<CoreCandleData> WavyCandles(int count)
    {
        var candles = new List<CoreCandleData>(count);
        for (int i = 0; i < count; i++)
        {
            decimal open = 100m + (i % 9) * 1.7m + (i % 4) * 0.6m + i * 0.15m;
            decimal close = open + ((i % 5) - 2) * 1.25m;
            decimal high = Math.Max(open, close) + 1m + (i % 4) * 0.5m;
            decimal low = Math.Min(open, close) - 1m - (i % 3) * 0.25m;
            candles.Add(new CoreCandleData(new DateTime(2024, 1, 1).AddDays(i), open, high, low, close, 1000 + i));
        }

        return candles;
    }

    public static IEnumerable<object[]> Sources() => new[]
    {
        new object[] { PriceType.HeikinAshiOpen, PriceType.Open },
        new object[] { PriceType.HeikinAshiHigh, PriceType.High },
        new object[] { PriceType.HeikinAshiLow, PriceType.Low },
        new object[] { PriceType.HeikinAshiClose, PriceType.Close },
    };

    private const int Count = 90;
    private const int StartIndex = 20;
    private const int EndIndex = 79;

    private static void Anchor(IChartObject obj, IReadOnlyList<CoreCandleData> candles, int startIndex = StartIndex, int endIndex = EndIndex)
    {
        obj.Points.Add(new ChartPoint(candles[startIndex].Timestamp, candles[startIndex].Close));
        obj.Points.Add(new ChartPoint(candles[endIndex].Timestamp, candles[endIndex].Close));
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void FftProjection(PriceType ha, PriceType plain)
    {
        var raw = WavyCandles(Count);
        var converted = HeikinAshiConverter.Convert(raw);
        var actual = new FftProjectionObject { PriceSource = ha };
        var expected = new FftProjectionObject { PriceSource = plain };
        Anchor(actual, raw);
        Anchor(expected, converted);

        actual.Recalculate(raw, TimeSpan.FromDays(1));
        expected.Recalculate(converted, TimeSpan.FromDays(1));

        Assert.NotEmpty(actual.ProjectedPath);
        Assert.Equal(expected.ProjectedPath, actual.ProjectedPath);
        Assert.Equal(expected.UpperBandPath, actual.UpperBandPath);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void HmmProjection(PriceType ha, PriceType plain)
    {
        var raw = WavyCandles(Count);
        var converted = HeikinAshiConverter.Convert(raw);
        var actual = new HmmProjectionObject { PriceSource = ha };
        var expected = new HmmProjectionObject { PriceSource = plain };
        Anchor(actual, raw);
        Anchor(expected, converted);

        actual.Recalculate(raw, TimeSpan.FromDays(1));
        expected.Recalculate(converted, TimeSpan.FromDays(1));

        Assert.NotEmpty(actual.ProjectedPath);
        Assert.Equal(expected.ProjectedPath, actual.ProjectedPath);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void SsaProjection(PriceType ha, PriceType plain)
    {
        var raw = WavyCandles(Count);
        var converted = HeikinAshiConverter.Convert(raw);
        var actual = new SsaProjectionObject { PriceSource = ha };
        var expected = new SsaProjectionObject { PriceSource = plain };
        Anchor(actual, raw);
        Anchor(expected, converted);

        actual.Recalculate(raw, TimeSpan.FromDays(1));
        expected.Recalculate(converted, TimeSpan.FromDays(1));

        Assert.NotEmpty(actual.ProjectedPath);
        Assert.Equal(expected.ProjectedPath, actual.ProjectedPath);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void PearsonProjection(PriceType ha, PriceType plain)
    {
        var raw = WavyCandles(Count);
        var converted = HeikinAshiConverter.Convert(raw);
        var actual = new PearsonProjectionObject { PriceSource = ha };
        var expected = new PearsonProjectionObject { PriceSource = plain };
        Anchor(actual, raw, 70, 84);
        Anchor(expected, converted, 70, 84);

        actual.Recalculate(raw, TimeSpan.FromDays(1));
        expected.Recalculate(converted, TimeSpan.FromDays(1));

        Assert.Equal(expected.HasMatch, actual.HasMatch);
        Assert.Equal(expected.ProjectedPath, actual.ProjectedPath);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void FrechetProjection(PriceType ha, PriceType plain)
    {
        var raw = WavyCandles(120);
        var converted = HeikinAshiConverter.Convert(raw);
        var actual = new FrechetProjectionObject { PriceSource = ha };
        var expected = new FrechetProjectionObject { PriceSource = plain };
        Anchor(actual, raw, 104, 119);
        Anchor(expected, converted, 104, 119);

        actual.Recalculate(raw, TimeSpan.FromDays(1));
        expected.Recalculate(converted, TimeSpan.FromDays(1));

        Assert.Equal(expected.HasMatch, actual.HasMatch);
        Assert.Equal(expected.ProjectedPath, actual.ProjectedPath);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void ClothoidCurveProjection(PriceType ha, PriceType plain)
    {
        var raw = WavyCandles(Count);
        var converted = HeikinAshiConverter.Convert(raw);
        var actual = new ClothoidCurveProjectionObject { PriceSource = ha };
        var expected = new ClothoidCurveProjectionObject { PriceSource = plain };
        Anchor(actual, raw);
        Anchor(expected, converted);

        actual.Recalculate(raw, TimeSpan.FromDays(1));
        expected.Recalculate(converted, TimeSpan.FromDays(1));

        Assert.NotEmpty(actual.ProjectedPath);
        Assert.Equal(expected.ProjectedPath, actual.ProjectedPath);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void SsaSupportResistance_DynamicEnvelopes(PriceType ha, PriceType plain)
    {
        var raw = WavyCandles(Count);
        var converted = HeikinAshiConverter.Convert(raw);
        var actual = new SsaDynamicEnvelopesObject { PriceSource = ha };
        var expected = new SsaDynamicEnvelopesObject { PriceSource = plain };
        Anchor(actual, raw);
        Anchor(expected, converted);

        actual.Recalculate(raw, TimeSpan.FromDays(1));
        expected.Recalculate(converted, TimeSpan.FromDays(1));

        Assert.NotNull(actual.CalculatedResult);
        Assert.Equal(expected.CalculatedResult!.UpperBand, actual.CalculatedResult!.UpperBand);
        Assert.Equal(expected.CalculatedResult.LowerBand, actual.CalculatedResult.LowerBand);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void SsaMultiComponent(PriceType ha, PriceType plain)
    {
        var raw = WavyCandles(Count);
        var converted = HeikinAshiConverter.Convert(raw);
        var actual = new SsaMultiComponentObject { PriceSource = ha };
        var expected = new SsaMultiComponentObject { PriceSource = plain };
        Anchor(actual, raw);
        Anchor(expected, converted);

        actual.Recalculate(raw, TimeSpan.FromDays(1));
        expected.Recalculate(converted, TimeSpan.FromDays(1));

        Assert.NotEmpty(actual.TrendPath);
        Assert.Equal(expected.TrendPath, actual.TrendPath);
        Assert.Equal(expected.CompositePath, actual.CompositePath);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void RangeSpline_ExtractedPoints(PriceType ha, PriceType plain)
    {
        var raw = WavyCandles(Count);
        var converted = HeikinAshiConverter.Convert(raw);
        var actual = new RangeSplineObject { PriceSource = ha };
        var expected = new RangeSplineObject { PriceSource = plain };
        Anchor(actual, raw);
        Anchor(expected, converted);

        actual.Recalculate(raw);
        expected.Recalculate(converted);

        Assert.Equal(EndIndex - StartIndex + 1, actual.ExtractedPoints.Count);
        Assert.Equal(expected.ExtractedPoints, actual.ExtractedPoints);
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void FftSpectrum_Bins(PriceType ha, PriceType plain)
    {
        var raw = WavyCandles(Count);
        var converted = HeikinAshiConverter.Convert(raw);
        var from = new ChartPoint(raw[StartIndex].Timestamp, raw[StartIndex].Close);
        var to = new ChartPoint(raw[EndIndex].Timestamp, raw[EndIndex].Close);
        var actual = new FftSpectrumObject(from, to) { PriceSource = ha };
        var expected = new FftSpectrumObject(from, to) { PriceSource = plain };

        actual.Recalculate(raw);
        expected.Recalculate(converted);

        Assert.NotEmpty(actual.SpectrumBins);
        Assert.Equal(expected.SpectrumBins, actual.SpectrumBins);
    }

    // The price types without a plain candle field: the source must equal the plain Close of candles whose Close is the series value.
    public static IEnumerable<object[]> DerivedSources() => new[]
    {
        new object[] { PriceType.TrueHigh },
        new object[] { PriceType.TrueLow },
        new object[] { PriceType.Midpoint },
        new object[] { PriceType.Average },
    };

    private static List<CoreCandleData> CandlesWithCloseFromSeries(IReadOnlyList<CoreCandleData> raw, PriceType source)
    {
        var series = StockAnalyzer.Core.Models.Indicators.PriceDataHelper.ExtractPriceSeries(raw, source);
        return raw.Select((c, i) => c with { Close = series[i]!.Value }).ToList();
    }

    [Theory]
    [MemberData(nameof(DerivedSources))]
    public void SsaMultiComponent_DerivedPriceTypes_UseTheSsotPrice(PriceType source)
    {
        var raw = WavyCandles(Count);
        var replaced = CandlesWithCloseFromSeries(raw, source);
        var actual = new SsaMultiComponentObject { PriceSource = source };
        var expected = new SsaMultiComponentObject { PriceSource = PriceType.Close };
        Anchor(actual, raw);
        Anchor(expected, replaced);

        actual.Recalculate(raw, TimeSpan.FromDays(1));
        expected.Recalculate(replaced, TimeSpan.FromDays(1));

        Assert.NotEmpty(actual.TrendPath);
        Assert.Equal(expected.TrendPath, actual.TrendPath);
        Assert.Equal(expected.CompositePath, actual.CompositePath);
    }

    [Theory]
    [InlineData(PriceType.TrueHigh)]
    [InlineData(PriceType.TrueLow)]
    public void RangeSpline_TrueHighLow_UseThePreviousClose(PriceType source)
    {
        var raw = WavyCandles(Count);
        var obj = new RangeSplineObject { PriceSource = source };
        Anchor(obj, raw);

        obj.Recalculate(raw);

        var series = StockAnalyzer.Core.Models.Indicators.PriceDataHelper.ExtractPriceSeries(raw, source);
        for (int k = 0; k < obj.ExtractedPoints.Count; k++)
        {
            Assert.Equal(series[StartIndex + k], obj.ExtractedPoints[k].Price);
        }
    }

    // The release path hands these tools a lazily mapped sequence (not an IReadOnlyList): for chronological candles the
    // materialized copy must give exactly what the list gives, including the recursive Heikin-Ashi sources.
    [Theory]
    [InlineData(PriceType.HeikinAshiOpen)]
    [InlineData(PriceType.HeikinAshiHigh)]
    [InlineData(PriceType.HeikinAshiLow)]
    [InlineData(PriceType.HeikinAshiClose)]
    public void RangeSpline_LazySequenceInput_EqualsListInput(PriceType ha)
    {
        var raw = WavyCandles(Count);
        var fromList = new RangeSplineObject { PriceSource = ha };
        var fromSequence = new RangeSplineObject { PriceSource = ha };
        Anchor(fromList, raw);
        Anchor(fromSequence, raw);

        fromList.Recalculate(raw);
        fromSequence.Recalculate(raw.Select(c => c));

        Assert.NotEmpty(fromList.ExtractedPoints);
        Assert.Equal(fromList.ExtractedPoints, fromSequence.ExtractedPoints);
    }

    [Theory]
    [InlineData(PriceType.HeikinAshiOpen)]
    [InlineData(PriceType.HeikinAshiHigh)]
    [InlineData(PriceType.HeikinAshiLow)]
    [InlineData(PriceType.HeikinAshiClose)]
    public void FftSpectrum_LazySequenceInput_EqualsListInput(PriceType ha)
    {
        var raw = WavyCandles(Count);
        var from = new ChartPoint(raw[StartIndex].Timestamp, raw[StartIndex].Close);
        var to = new ChartPoint(raw[EndIndex].Timestamp, raw[EndIndex].Close);
        var fromList = new FftSpectrumObject(from, to) { PriceSource = ha };
        var fromSequence = new FftSpectrumObject(from, to) { PriceSource = ha };

        fromList.Recalculate(raw);
        fromSequence.Recalculate(raw.Select(c => c));

        Assert.NotEmpty(fromList.SpectrumBins);
        Assert.Equal(fromList.SpectrumBins, fromSequence.SpectrumBins);
    }

    [Fact]
    public void NonHeikinAshiSource_RangeSpline_IsUnchangedFromPerBarExtraction()
    {
        var raw = WavyCandles(Count);
        var obj = new RangeSplineObject { PriceSource = PriceType.Typical };
        Anchor(obj, raw);

        obj.Recalculate(raw);

        for (int k = 0; k < obj.ExtractedPoints.Count; k++)
        {
            var c = raw[StartIndex + k];
            Assert.Equal((c.High + c.Low + c.Close) / 3.0m, obj.ExtractedPoints[k].Price);
        }
    }
}
