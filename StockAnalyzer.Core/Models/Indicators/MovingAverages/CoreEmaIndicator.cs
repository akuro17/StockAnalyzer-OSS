using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Constants;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.EMA)]
public class CoreEmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;

    public override string Name => $"EMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter param)
        {
            Period = param.Period;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (dynamicPeriods != null && dynamicPeriods.Count > 0)
        {
            var dynamicResults = AdaptiveSmoothingHelper.CalculateAdaptiveEma(series, dynamicPeriods, Period);
            _values.Clear();
            _values.AddRange(dynamicResults);
            return IndicatorResult.Success(_values);
        }

        var results = CalculateParallel(series, CalculateSegment);

        _values.Clear();
        _values.AddRange(results);

        return IndicatorResult.Success(_values);
    }

    private List<decimal?> CalculateSegment(IReadOnlyList<decimal?> series, int start, int end)
    {
        var results = new List<decimal?>(end - start);

        int overlap = Period * ChartConstants.EmaConvergenceMultiplier;
        int calcStart = Math.Max(0, start - overlap);

        decimal? ema = null;
        decimal multiplier = 2m / (Period + 1);

        for (int i = calcStart; i < end; i++)
        {
            if (i < Period - 1)
            {
                ema = null;
            }
            else if (i == Period - 1)
            {
                decimal sum = 0;
                for (int j = 0; j < Period; j++)
                    sum += series[i - j] ?? 0m;
                ema = sum / Period;
            }
            else
            {
                if (ema.HasValue)
                {
                    decimal price = series[i] ?? 0m;
                    ema = (price - ema.Value) * multiplier + ema.Value;
                }
                else
                {
                    if (i >= Period - 1)
                    {
                        decimal sum = 0;
                        for (int j = 0; j < Period; j++)
                            sum += series[i - j] ?? 0m;
                        ema = sum / Period;
                    }
                    else
                    {
                        ema = null;
                    }
                }
            }

            if (i >= start)
            {
                results.Add(ema);
            }
        }

        return results;
    }
}
