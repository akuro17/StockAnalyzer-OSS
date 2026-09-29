using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Utilities;

namespace StockAnalyzer.Core.Models.Indicators.Volatility;

/// <summary>
/// True Range indicator: True High - True Low.
/// Rendered in a sub-window panel as a volatility indicator.
/// </summary>
[StockAnalyzerIndicator(IndicatorType.TrueRange)]
public class CoreTrueRangeIndicator : CoreIndicatorBase
{
    public override string Name => "True Range";

    public override bool IsOverlay => false;

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        // Parameterless indicator
    }

    protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
    {
        _values.Clear();
        if (candles == null || candles.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        if (_values.Capacity < candles.Count)
        {
            _values.Capacity = candles.Count;
        }

        // Bar 0: Wilder's standard (High - Low)
        _values.Add(AtrCalculator.CalculateTrueRange(candles[0].High, candles[0].Low, null));

        // Bar 1..N-1: Max(H - L, |H - C_prev|, |L - C_prev|)
        for (int i = 1; i < candles.Count; i++)
        {
            _values.Add(AtrCalculator.CalculateTrueRange(candles[i].High, candles[i].Low, candles[i - 1].Close));
        }

        return IndicatorResult.Success(_values);
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        if (series == null || series.Count == 0)
        {
            return IndicatorResult.Success(_values);
        }

        if (_values.Capacity < series.Count)
        {
            _values.Capacity = series.Count;
        }

        // Bar 0: Preceding price does not exist -> strictly null
        _values.Add(null);
        for (int i = 1; i < series.Count; i++)
        {
            if (series[i].HasValue && series[i - 1].HasValue)
            {
                _values.Add(Math.Abs(series[i]!.Value - series[i - 1]!.Value));
            }
            else
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
