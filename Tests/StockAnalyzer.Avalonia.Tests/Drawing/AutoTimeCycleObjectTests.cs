using System;
using System.Collections.Generic;
using Avalonia.Media;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Avalonia.Drawing.Objects;
using StockAnalyzer.Core.Models;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

public class AutoTimeCycleObjectTests
{
    private static List<CoreCandleData> MakeSineCandles(int count, double period = 40.0, double amplitude = 10.0, double center = 100.0)
    {
        var list = new List<CoreCandleData>(count);
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0);

        for (int i = 0; i < count; i++)
        {
            double angle = 2.0 * Math.PI * i / period;
            decimal price = (decimal)(center + amplitude * Math.Sin(angle));

            list.Add(new CoreCandleData(
                baseTime.AddDays(i),
                price,
                price + 1m,
                price - 1m,
                price,
                1000
            ));
        }

        return list;
    }

    [Fact]
    public void AutoTimeCycleObject_InitialDefaults()
    {
        var obj = new AutoTimeCycleObject();

        Assert.Equal(ChartObjectType.AutoTimeCycle, obj.Type);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.Color);
        Assert.Equal(1.5, obj.Thickness);
        Assert.Equal(DrawingThemeContext.DefaultColor, obj.FillColor);
        Assert.Equal(10, obj.FillOpacity);
        Assert.Equal(PriceType.Median, obj.PriceSource);
        Assert.True(obj.ApplyDetrend);
        Assert.Equal(5.0, obj.MinPeriod);
        Assert.Equal(200.0, obj.MaxPeriod);
        Assert.Equal(10, obj.CycleCount);
        Assert.Equal(AutoCycleAlignment.Endpoint, obj.Alignment);
        Assert.True(obj.EnableFrequencyInterpolation);
        Assert.True(obj.ShowPeriodLabel);
        Assert.Equal(0.0, obj.DominantPeriod);
        Assert.Equal(0.0, obj.DominantPower);
        Assert.Equal(0.0, obj.PowerShare);
        Assert.Empty(obj.ProjectedBarIndices);
        Assert.False(obj.IsCalculated);
    }

    [Fact]
    public void AutoTimeCycleObject_Recalculate_SyntheticSineWave_IdentifiesDominantPeriod()
    {
        // 120 bars with a clean 40-bar periodic wave
        var candles = MakeSineCandles(120, period: 40.0, amplitude: 15.0);
        var obj = new AutoTimeCycleObject
        {
            MinPeriod = 5.0,
            MaxPeriod = 100.0,
            CycleCount = 5,
            Alignment = AutoCycleAlignment.Endpoint
        };

        // Select full range (index 0 to 119)
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[119].Timestamp, candles[119].Close));

        obj.Recalculate(candles);

        Assert.True(obj.IsCalculated);
        // Dominant period should be within 0.5 bars of the true 40.0 period
        Assert.InRange(obj.DominantPeriod, 39.5, 40.5);
        Assert.True(obj.PowerShare > 50.0, $"Expected dominant power share > 50%, actual: {obj.PowerShare}%");
        Assert.Equal(5, obj.ProjectedBarIndices.Count);

        // First projected line should be roughly 119 + 40 = 159
        double expectedFirst = 119.0 + obj.DominantPeriod;
        Assert.InRange(obj.ProjectedBarIndices[0], expectedFirst - 0.5, expectedFirst + 0.5);

        // Values should increment by dominant period
        for (int i = 1; i < obj.ProjectedBarIndices.Count; i++)
        {
            double diff = obj.ProjectedBarIndices[i] - obj.ProjectedBarIndices[i - 1];
            Assert.InRange(diff, 39.0, 41.0);
        }
    }

    [Fact]
    public void AutoTimeCycleObject_Recalculate_FailsafeGuards_HandlesInvalidInputsGracefully()
    {
        var candles = MakeSineCandles(50, period: 20.0);
        var obj = new AutoTimeCycleObject();

        // 1. Null candles
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[20].Timestamp, candles[20].Close));
        obj.Recalculate(null);
        Assert.False(obj.IsCalculated);

        // 2. Short selection (N = 3 bars < 4)
        obj.Points.Clear();
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[2].Timestamp, candles[2].Close));
        obj.Recalculate(candles);
        Assert.False(obj.IsCalculated);
        Assert.Empty(obj.ProjectedBarIndices);

        // 3. Constant signal (Zero-variance horizontal line)
        var constantCandles = new List<CoreCandleData>();
        for (int i = 0; i < 30; i++)
        {
            constantCandles.Add(new CoreCandleData(
                DateTime.Now.AddDays(i), 100m, 100m, 100m, 100m, 1000));
        }
        obj.Points.Clear();
        obj.Points.Add(new ChartPoint(constantCandles[0].Timestamp, constantCandles[0].Close));
        obj.Points.Add(new ChartPoint(constantCandles[20].Timestamp, constantCandles[20].Close));
        obj.Recalculate(constantCandles);
        Assert.False(obj.IsCalculated);

        // 4. Reversed selection range (t_start > t_end) should automatically normalize
        obj.Points.Clear();
        obj.Points.Add(new ChartPoint(candles[39].Timestamp, candles[39].Close));
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Recalculate(candles);
        Assert.True(obj.IsCalculated);
        Assert.NotEmpty(obj.ProjectedBarIndices);
    }

    [Fact]
    public void AutoTimeCycleObject_AlignmentModes_GenerateDeterministicFutureIndices()
    {
        var candles = MakeSineCandles(120, period: 40.0);

        var objEndpoint = new AutoTimeCycleObject { Alignment = AutoCycleAlignment.Endpoint, CycleCount = 3 };
        objEndpoint.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        objEndpoint.Points.Add(new ChartPoint(candles[119].Timestamp, candles[119].Close));
        objEndpoint.Recalculate(candles);

        var objPeak = new AutoTimeCycleObject { Alignment = AutoCycleAlignment.Peak, CycleCount = 3 };
        objPeak.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        objPeak.Points.Add(new ChartPoint(candles[119].Timestamp, candles[119].Close));
        objPeak.Recalculate(candles);

        var objTrough = new AutoTimeCycleObject { Alignment = AutoCycleAlignment.Trough, CycleCount = 3 };
        objTrough.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        objTrough.Points.Add(new ChartPoint(candles[119].Timestamp, candles[119].Close));
        objTrough.Recalculate(candles);

        Assert.True(objEndpoint.IsCalculated);
        Assert.True(objPeak.IsCalculated);
        Assert.True(objTrough.IsCalculated);

        // All projected indices must be strictly beyond the in-sample end bar (119)
        Assert.True(objEndpoint.ProjectedBarIndices[0] > 119.0);
        Assert.True(objPeak.ProjectedBarIndices[0] > 119.0);
        Assert.True(objTrough.ProjectedBarIndices[0] > 119.0);

        // Peak and Trough indices must differ by approximately half a period (~20 bars)
        double diff = Math.Abs(objPeak.ProjectedBarIndices[0] - objTrough.ProjectedBarIndices[0]);
        Assert.True(diff >= 15.0 && diff <= 25.0, $"Expected peak-trough difference near 20 bars, actual: {diff}");
    }

    [Fact]
    public void AutoTimeCycleObject_NonZeroStartIndex_MaintainsPhaseAlignment()
    {
        // 250 bars with period 40.
        // Sine wave: angle = 2*PI*i / 40.
        // Crests (peaks) occur at angle = PI/2 + 2*PI*k -> i = 10, 50, 90, 130, 170, 210, 250...
        var candles = MakeSineCandles(250, period: 40.0);

        var objPeak = new AutoTimeCycleObject
        {
            Alignment = AutoCycleAlignment.Peak,
            CycleCount = 3
        };

        // Select range [80..199] (exactly 120 bars = 3 integer 40-bar cycles).
        // startIndex = 80, endIndex = 199.
        // Inside selection, crests are at i = 90, 130, 170.
        // The first crest beyond endIndex (199) is at i = 210.
        objPeak.Points.Add(new ChartPoint(candles[80].Timestamp, candles[80].Close));
        objPeak.Points.Add(new ChartPoint(candles[199].Timestamp, candles[199].Close));

        objPeak.Recalculate(candles);

        Assert.True(objPeak.IsCalculated);
        Assert.Equal(3, objPeak.ProjectedBarIndices.Count);

        // All projected lines must be strictly greater than endIndex (199)
        Assert.True(objPeak.ProjectedBarIndices[0] > 199.0);

        // The first projected peak should align with bar 210 (within 1 bar)
        Assert.InRange(objPeak.ProjectedBarIndices[0], 209.0, 211.0);

        // Subsequent peaks should step by approx 40 bars
        double diff = objPeak.ProjectedBarIndices[1] - objPeak.ProjectedBarIndices[0];
        Assert.InRange(diff, 39.0, 41.0);
    }

    [Fact]
    public void AutoTimeCycleObject_GetCalculatedValues_ReturnsAccurateValues()
    {
        var candles = MakeSineCandles(80, period: 20.0);
        var obj = new AutoTimeCycleObject();
        obj.Points.Add(new ChartPoint(candles[0].Timestamp, candles[0].Close));
        obj.Points.Add(new ChartPoint(candles[79].Timestamp, candles[79].Close));

        // Before calculation
        var initialValues = obj.GetCalculatedValues(DateTime.Now);
        Assert.Empty(initialValues);

        // After calculation
        obj.Recalculate(candles);
        var values = obj.GetCalculatedValues(DateTime.Now);
        Assert.Equal(2, values.Count);
        Assert.Equal("Dominant Cycle Period", values[0].Label);
        Assert.Equal("Cycle Power Share", values[1].Label);
    }
}
