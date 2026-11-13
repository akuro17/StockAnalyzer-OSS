using System;
using System.Collections.Generic;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.Chart;
using StockAnalyzer.Core.Models.Parameters;
using Xunit;

namespace StockAnalyzer.Avalonia.Tests.Drawing;

/// <summary>
/// Plan 02 T2: the indicator's Week / Month levels of the last bar equal the Classic Pivot Points drawing tool's levels
/// (same period boundaries: ISO week starting Monday, calendar month; the drawing tool is not changed).
/// </summary>
public class ClassicPivotPointsIndicatorParityTests
{
    private static List<CoreCandleData> Candles()
    {
        var list = new List<CoreCandleData>();
        var day = new DateTime(2026, 7, 1);
        for (int i = 0; i < 120; i++)
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                day = day.AddDays(1);
                i--;
                continue;
            }

            decimal close = 100m + (i * 5 % 17) + i * 0.1m;
            list.Add(new CoreCandleData(day, close - 1m, close + 2m + (i % 4), close - 2m - (i % 3), close, 1000));
            day = day.AddDays(1);
        }

        return list;
    }

    [Theory]
    [InlineData(PivotSourcePeriod.Week, PivotPeriod.Week)]
    [InlineData(PivotSourcePeriod.Month, PivotPeriod.Month)]
    public void LastBarLevels_EqualTheDrawingToolLevels(PivotSourcePeriod sourcePeriod, PivotPeriod drawingPeriod)
    {
        List<CoreCandleData> candles = Candles();

        var indicator = new CoreClassicPivotPointsIndicator();
        indicator.Configure(new CoreClassicPivotPointsParameter { SourcePeriod = sourcePeriod });
        var result = indicator.Calculate(candles);

        // Check several bars, not only the last one: each bar's levels equal the tool anchored at that bar.
        foreach (int index in new[] { candles.Count - 1, candles.Count - 8, candles.Count / 2 })
        {
            var tool = new ClassicPivotPointsObject(new ChartPoint(candles[index].Timestamp, candles[index].Close)) { Period = drawingPeriod };
            tool.Recalculate(candles);
            Assert.True(tool.HasValidData);

            Assert.Equal(tool.Pivot, result.GetSeries("Main")[index]);
            Assert.Equal(tool.R1, result.GetSeries("R1")[index]);
            Assert.Equal(tool.R2, result.GetSeries("R2")[index]);
            Assert.Equal(tool.R3, result.GetSeries("R3")[index]);
            Assert.Equal(tool.S1, result.GetSeries("S1")[index]);
            Assert.Equal(tool.S2, result.GetSeries("S2")[index]);
            Assert.Equal(tool.S3, result.GetSeries("S3")[index]);
        }
    }
}
