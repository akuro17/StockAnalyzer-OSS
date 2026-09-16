using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Avalonia.Common;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Drawing.Behaviors;

// --- Analysis / Recalculation Tools ---

public sealed class RegressionTrendBehavior : TwoClickBehavior<RegressionTrendObject>
{
    protected override RegressionTrendObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? candles)
    {
        var regression = new RegressionTrendObject(p, p);
        if (candles != null) regression.Recalculate(candles);
        return regression;
    }

    protected override void OnPointUpdated(RegressionTrendObject obj, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        if (candles != null) obj.Recalculate(candles);
    }
}

public sealed class RangeSplineBehavior : TwoClickBehavior<RangeSplineObject>
{
    protected override RangeSplineObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? candles)
    {
        var spline = new RangeSplineObject(p, p);
        if (candles != null) spline.Recalculate(candles);
        return spline;
    }

    protected override void OnPointUpdated(RangeSplineObject obj, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        if (candles != null) obj.Recalculate(candles);
    }
}

public sealed class FixedRangeVolumeBehavior : TwoClickBehavior<FixedRangeVolumeProfileObject>
{
    protected override FixedRangeVolumeProfileObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FixedRangeVolumeProfileObject(p, p);

    protected override void OnPointUpdated(FixedRangeVolumeProfileObject obj, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        if (candles != null) obj.Recalculate(candles);
    }
}

public sealed class FftSpectrumBehavior : TwoClickBehavior<FftSpectrumObject>
{
    protected override FftSpectrumObject CreateInstance(ChartPoint p, IEnumerable<CoreCandleData>? _)
        => new FftSpectrumObject(p, p);

    protected override void OnPointUpdated(FftSpectrumObject obj, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        if (candles != null) obj.Recalculate(candles);
    }
}

// --- Prediction Tools ---

public sealed class LongShortPositionBehavior : ClickToPlaceBehavior<LongShortPositionObject>
{
    private readonly bool _isLong;

    public LongShortPositionBehavior(bool isLong)
    {
        _isLong = isLong;
    }

    protected override LongShortPositionObject CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? _)
    {
        decimal price = chartPoint.Price;
        decimal offset = price * ChartConstants.LongShortInitialOffsetPercent;
        if (offset == 0) offset = 10;

        ChartPoint stop, target;
        if (_isLong)
        {
            stop = new ChartPoint(chartPoint.Time, price - offset);
            target = new ChartPoint(chartPoint.Time, price + offset);
        }
        else
        {
            stop = new ChartPoint(chartPoint.Time, price + offset);
            target = new ChartPoint(chartPoint.Time, price - offset);
        }

        return new LongShortPositionObject(chartPoint, stop, target, _isLong);
    }
}

public sealed class GhostFeedBehavior : TwoClickBehavior<GhostFeedObject>
{
    protected override GhostFeedObject CreateInstance(ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        return new GhostFeedObject(chartPoint, chartPoint);
    }

    protected override void OnPointUpdated(GhostFeedObject obj, ChartPoint chartPoint, IEnumerable<CoreCandleData>? candles)
    {
        obj.UpdateEndPoint(chartPoint, candles);
    }
}
