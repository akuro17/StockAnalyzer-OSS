using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Analysis;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// WebAI UI-exposure Batch 1: new Data Window rows / settings of the SSA and Hough drawing tools.
/// </summary>
public class UiExposureBatch1DrawingTests
{
    private const string SeparabilityKey = "SSA Separability";
    private const string StabilityKey = "SSA Stability (ν²)";

    private static List<CoreCandleData> CreateWaveCandles(int count)
    {
        var list = new List<CoreCandleData>(count);
        var baseTime = new DateTime(2024, 1, 1);
        for (int i = 0; i < count; i++)
        {
            decimal price = 100m + i * 0.4m
                + (decimal)(4.0 * Math.Sin(2.0 * Math.PI * i / 9.0))
                + (decimal)(1.5 * Math.Sin(2.0 * Math.PI * i / 5.0));
            list.Add(new CoreCandleData(baseTime.AddHours(i), price - 1m, price + 2m, price - 2m, price, 1000 + i));
        }

        return list;
    }

    private static T Select<T>(T obj, IReadOnlyList<CoreCandleData> candles, int from, int to) where T : IChartObject
    {
        obj.Points.Add(new ChartPoint(candles[from].Timestamp, candles[from].Close));
        obj.Points.Add(new ChartPoint(candles[to].Timestamp, candles[to].Close));
        return obj;
    }

    private static void Recalc(IChartObject obj, IReadOnlyList<CoreCandleData> candles)
    {
        switch (obj)
        {
            case SsaMultiComponentObject m: m.Recalculate(candles, TimeSpan.FromHours(1)); break;
            case SsaProjectionObject p: p.Recalculate(candles, TimeSpan.FromHours(1)); break;
            case SsaSupportResistanceObject s: s.Recalculate(candles, TimeSpan.FromHours(1)); break;
            case HoughAutoLinesObject h: h.Recalculate(candles); break;
            case HoughParabolicCurveObject c: c.Recalculate(candles); break;
            default: throw new NotSupportedException(obj.GetType().Name);
        }
    }

    // ---------------------------------------------------------------- SsaMultiComponentObject

    [Fact]
    public void SsaMultiComponent_Defaults_AutoRankOff()
    {
        var obj = new SsaMultiComponentObject();

        Assert.False(obj.AutoRank);
        Assert.Equal(SsaRankSelectionMethod.CumulativeEnergy, obj.RankMethod);
    }

    [Fact]
    public void SsaMultiComponent_WithSeveralComponents_ExposesSeparabilityRow()
    {
        var candles = CreateWaveCandles(60);
        var obj = Select(new SsaMultiComponentObject { EmbeddingDimension = 12, NumComponents = 4 }, candles, 0, 49);
        Recalc(obj, candles);

        Assert.Equal(4, obj.EffectiveRank);
        Assert.InRange(obj.SeparabilityScore, 0.0, 100.0);

        var row = Assert.Single(obj.GetCalculatedValues(DateTime.UtcNow), v => v.Key == SeparabilityKey);
        Assert.Equal((decimal)obj.SeparabilityScore, row.NumericValue);
        Assert.Contains($"{obj.SeparabilityScore:F1}%", row.FormattedText);
        Assert.Contains(SsaDiagnostics.GetSeparabilityGrade(obj.SeparabilityScore), row.FormattedText);
    }

    [Fact]
    public void SsaMultiComponent_SingleComponent_HasNoSeparabilityRow()
    {
        var candles = CreateWaveCandles(60);
        var obj = Select(new SsaMultiComponentObject { EmbeddingDimension = 12, NumComponents = 1 }, candles, 0, 49);
        Recalc(obj, candles);

        Assert.Equal(1, obj.EffectiveRank);
        Assert.Equal(100.0, obj.SeparabilityScore);
        Assert.DoesNotContain(obj.GetCalculatedValues(DateTime.UtcNow), v => v.Key == SeparabilityKey);
    }

    [Theory]
    [InlineData(SsaRankSelectionMethod.KaiserGuttman)]
    [InlineData(SsaRankSelectionMethod.ScreeMaxCurvature)]
    [InlineData(SsaRankSelectionMethod.CumulativeEnergy)]
    public void SsaMultiComponent_AutoRank_OverridesNumComponents_WithinBounds(SsaRankSelectionMethod method)
    {
        var candles = CreateWaveCandles(60);
        var obj = Select(new SsaMultiComponentObject { EmbeddingDimension = 12, NumComponents = 1, AutoRank = true, RankMethod = method }, candles, 0, 49);
        Recalc(obj, candles);

        Assert.InRange(obj.EffectiveRank, 1, 11);
        Assert.NotEmpty(obj.TrendPath);
        var rankRow = Assert.Single(obj.GetCalculatedValues(DateTime.UtcNow), v => v.Key == "Components (r)");
        Assert.Equal(obj.EffectiveRank, (int)rankRow.NumericValue!.Value);
    }

    // ---------------------------------------------------------------- SsaProjectionObject

    [Fact]
    public void SsaProjection_AutoRank_DrivesEffectiveComponents_AndKeepsStabilityRow()
    {
        var candles = CreateWaveCandles(60);
        var off = Select(new SsaProjectionObject { EmbeddingDimension = 10, NumComponents = 2 }, candles, 0, 49);
        var on = Select(new SsaProjectionObject { EmbeddingDimension = 10, NumComponents = 1, AutoRank = true }, candles, 0, 49);
        Recalc(off, candles);
        Recalc(on, candles);

        Assert.False(new SsaProjectionObject().AutoRank);
        Assert.Equal(2, off.Components.Count);
        Assert.InRange(on.Components.Count, 1, 9);
        Assert.Single(on.GetCalculatedValues(DateTime.UtcNow), v => v.Key == StabilityKey);
        Assert.Single(off.GetCalculatedValues(DateTime.UtcNow), v => v.Key == StabilityKey);
    }

    // ---------------------------------------------------------------- SsaSupportResistanceObject

    [Fact]
    public void SsaSupportResistance_ExposesStabilityRow_MatchingTheEngineResult()
    {
        var candles = CreateWaveCandles(60);
        var obj = Select(new SsaSupportResistanceObject { EmbeddingDimension = 12 }, candles, 0, 49);
        Recalc(obj, candles);

        Assert.NotNull(obj.CalculatedResult);
        var row = Assert.Single(obj.GetCalculatedValues(DateTime.UtcNow), v => v.Key == StabilityKey);

        Assert.Equal((decimal)obj.CalculatedResult!.NuSquared, row.NumericValue);
        Assert.Contains($"{obj.CalculatedResult.NuSquared:F3}", row.FormattedText);
        Assert.Contains(obj.CalculatedResult.IsStable ? "Stable" : "Caution", row.FormattedText);
        Assert.Equal(obj.CalculatedResult.IsStable ? IndicatorColor.Bullish : IndicatorColor.Bearish, row.Color);
    }

    // ---------------------------------------------------------------- Hough

    [Fact]
    public void HoughAutoLines_UseVolumeWeight_DefaultsOff_AndRecalculatesWhenOn()
    {
        var candles = CreateWaveCandles(60);
        Assert.False(new HoughAutoLinesObject().UseVolumeWeight);

        var weighted = Select(new HoughAutoLinesObject { UseVolumeWeight = true }, candles, 0, 59);
        Recalc(weighted, candles);

        Assert.NotNull(weighted.CalculatedResult);
    }

    [Fact]
    public void HoughAutoLines_ChannelRows_FollowTheTopChannel_AndTheShowChannelsFlag()
    {
        var candles = CreateWaveCandles(80);
        var obj = Select(new HoughAutoLinesObject { PivotWindow = 2, VoteThreshold = 2, MaxLines = 8, ShowChannels = true }, candles, 0, 79);
        Recalc(obj, candles);

        var result = obj.CalculatedResult;
        Assert.NotNull(result);
        Assert.True(result!.Channels.Count > 0, "The wave fixture is expected to yield at least one parallel channel.");

        var channel = result.Channels[0];
        int lastBar = 79;
        decimal lastClose = candles[79].Close;
        var values = obj.GetCalculatedValues(DateTime.UtcNow);

        var width = Assert.Single(values, v => v.Key == "Channel #1 Width");
        Assert.Equal(Math.Round(channel.GetWidthAt(lastBar), 2), width.NumericValue);
        Assert.Contains($"Score={channel.RelativePosition * 100.0:F0}%", width.FormattedText);

        var position = Assert.Single(values, v => v.Key == "Channel #1 Position");
        Assert.Equal(Math.Round(channel.GetPricePositionPercent(lastBar, lastClose), 1), position.NumericValue);

        obj.ShowChannels = false;
        var hidden = obj.GetCalculatedValues(DateTime.UtcNow);
        Assert.DoesNotContain(hidden, v => v.Key.StartsWith("Channel #1", StringComparison.Ordinal));
        Assert.Contains(hidden, v => v.Key == "Detected Channels");
    }

    [Fact]
    public void HoughParabolic_ValueText_ReportsVertexTimingRelativeToTheEndBar()
    {
        var candles = new List<CoreCandleData>(50);
        var baseDate = new DateTime(2025, 1, 1);
        for (int i = 0; i < 50; i++)
        {
            decimal trend = (decimal)(0.05 * Math.Pow(i - 25, 2) + 100.0);
            decimal wiggle = (i % 4 == 0) ? 2.0m : (i % 4 == 2 ? -2.0m : 0.0m);
            decimal close = trend + wiggle;
            candles.Add(new CoreCandleData(baseDate.AddDays(i), close, close + 1.5m, close - 1.5m, close, 1000L));
        }

        var obj = Select(new HoughParabolicCurveObject(), candles, 0, 49);
        Recalc(obj, candles);

        var result = obj.CalculatedResult;
        Assert.NotNull(result);
        var values = obj.GetCalculatedValues(DateTime.UtcNow).Where(v => v.Key.StartsWith("Parabola #", StringComparison.Ordinal)).ToList();
        Assert.Equal(result!.Parabolas.Count, values.Count);

        for (int i = 0; i < values.Count; i++)
        {
            int offset = result.Parabolas[i].VertexBar - result.Parabolas[i].EndBar;
            string expected = offset > 0 ? $"+{offset} bars (Future)" : offset < 0 ? $"{offset} bars (Past)" : "At EndBar";
            Assert.Contains($"ΔBar={expected}", values[i].FormattedText);
        }
    }
}
