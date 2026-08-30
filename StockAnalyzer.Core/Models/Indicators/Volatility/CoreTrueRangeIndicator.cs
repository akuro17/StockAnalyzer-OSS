using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

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

        for (int i = 0; i < candles.Count; i++)
        {
            decimal th = i == 0 ? candles[0].High : Math.Max(candles[i].High, candles[i - 1].Close);
            decimal tl = i == 0 ? candles[0].Low : Math.Min(candles[i].Low, candles[i - 1].Close);
            _values.Add(th - tl);
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

        _values.Add(0m);
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
