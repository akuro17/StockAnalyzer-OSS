using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

/// <summary>
/// 3-Step placement behavior for CatenaryCurveObject:
/// Step 1: Click sets P0 (start), drag updates P1 (end), and P2 (sag point) is automatically placed at midpoint.
/// Step 2: Click fixes P1 (end), drag updates P2 (sag price) with Time locked at midpoint between P0 and P1.
/// Step 3: Click fixes P2 (sag point) and finishes tool.
/// </summary>
public sealed class CatenaryCurveBehavior : ThreePointBehavior<CatenaryCurveObject>
{
    protected override CatenaryCurveObject CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? _)
        => new CatenaryCurveObject(chartPoint, chartPoint, chartPoint);

    protected override void OnPointUpdated(CatenaryCurveObject obj, int drawingStep, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        if (drawingStep <= 1)
        {
            // Step 1: P2 (sag point) auto-follows the exact P0-P1 midpoint instead of the raw cursor
            long midTicks = (obj.Points[0].Time.Ticks + chartPoint.Time.Ticks) / 2;
            decimal midPrice = (obj.Points[0].Price + chartPoint.Price) / 2m;
            obj.Points[2] = new ChartPoint(new DateTime(midTicks), midPrice);
        }
        else if (drawingStep == 2)
        {
            // Step 2: P2.Time stays locked to the P0-P1 midpoint; only the sag price follows the cursor
            long midTicks = (obj.Points[0].Time.Ticks + obj.Points[1].Time.Ticks) / 2;
            obj.Points[2] = new ChartPoint(new DateTime(midTicks), chartPoint.Price);
        }
    }
}
