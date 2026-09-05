using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.HMA)]
public class CoreHmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"HMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter param)
        {
            Period = param.Period;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

        int halfPeriod = Period / 2;
        int sqrtPeriod = (int)Math.Sqrt(Period);

        var wma1 = CalculateWma(series, halfPeriod);
        var wma2 = CalculateWma(series, Period);

        var diff = new List<decimal?>(series.Count);
        for (int i = 0; i < series.Count; i++)
        {
            if (wma1[i].HasValue && wma2[i].HasValue)
                diff.Add(2 * wma1[i]!.Value - wma2[i]!.Value);
            else
                diff.Add(null);
        }

        // WMA of diff
        decimal weightSum = (decimal)sqrtPeriod * (sqrtPeriod + 1) / 2m;
        for (int i = 0; i < diff.Count; i++)
        {
            if (i < (Period - 1) + (sqrtPeriod - 1))
            {
                _values.Add(null);
                continue;
            }

            decimal sum = 0;
            bool canCalculate = true;
            for (int j = 0; j < sqrtPeriod; j++)
            {
                if (!diff[i - j].HasValue)
                {
                    canCalculate = false;
                    break;
                }
                sum += diff[i - j]!.Value * (sqrtPeriod - j);
            }

            if (canCalculate && weightSum > 0)
            {
                _values.Add(sum / weightSum);
            }
            else
            {
                _values.Add(null);
            }
        }

        return IndicatorResult.Success(_values);
    }

    private static List<decimal?> CalculateWma(IReadOnlyList<decimal?> series, int period)
    {
        var result = new List<decimal?>(series.Count);
        if (period <= 0)
        {
            result.AddRange(Enumerable.Repeat<decimal?>(null, series.Count));
            return result;
        }
        decimal weightSum = (decimal)period * (period + 1) / 2m;

        for (int i = 0; i < series.Count; i++)
        {
            if (i < period - 1)
            {
                result.Add(null);
                continue;
            }
            decimal sum = 0;
            bool valid = true;
            for (int j = 0; j < period; j++)
            {
                var val = series[i - j];
                if (!val.HasValue)
                {
                    valid = false;
                    break;
                }
                sum += val.Value * (period - j);
            }
            result.Add(valid && weightSum > 0 ? (decimal?)(sum / weightSum) : null);
        }
        return result;
    }
}
