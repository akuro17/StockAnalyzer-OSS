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

        if (period <= 0)
        {
            for (int i = start; i < end; i++) results.Add(null);
            return results;
        }

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
                bool allValid = true;
                for (int j = 0; j < period; j++)
                {
                    var val = series[i - j];
                    if (!val.HasValue)
                    {
                        allValid = false;
                        break;
                    }
                    currentSum += val.Value;
                }
                isSumValid = allValid;
            }
            else
            {
                var oldVal = series[i - period];
                var newVal = series[i];
                if (!oldVal.HasValue || !newVal.HasValue)
                {
                    isSumValid = false;
                    currentSum = 0;
                    bool allValid = true;
                    for (int j = 0; j < period; j++)
                    {
                        var val = series[i - j];
                        if (!val.HasValue)
                        {
                            allValid = false;
                            break;
                        }
                        currentSum += val.Value;
                    }
                    isSumValid = allValid;
                }
                else
                {
                    currentSum = currentSum - oldVal.Value + newVal.Value;
                }
            }

            results.Add(isSumValid ? currentSum / period : null);
        }

        return results;
    }
}
