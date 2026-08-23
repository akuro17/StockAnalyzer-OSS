using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.SMA)]
public class CoreSmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;

    public override string Name => $"SMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter smaParam)
        {
            Period = smaParam.Period;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        if (dynamicPeriods != null && dynamicPeriods.Count > 0)
        {
            var dynamicResults = AdaptiveSmoothingHelper.CalculateAdaptiveSma(series, dynamicPeriods, Period);
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
        decimal currentSum = 0;
        bool isSumValid = false;
        int period = Period;

        for (int i = start; i < end; i++)
        {
            if (i < period - 1)
            {
                results.Add(null);
                continue;
            }

            if (!isSumValid)
            {
                currentSum = 0;
                for (int j = 0; j < period; j++)
                {
                    currentSum += series[i - j] ?? 0m;
                }
                isSumValid = true;
            }
            else
            {
                currentSum -= series[i - period] ?? 0m;
                currentSum += series[i] ?? 0m;
            }

            results.Add(currentSum / period);
        }

        return results;
    }
}
