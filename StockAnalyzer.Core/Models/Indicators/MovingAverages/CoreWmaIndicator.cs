using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.WMA)]
public class CoreWmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;

    public override string Name => $"WMA ({Period})";

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

        int weightSum = Period * (Period + 1) / 2;

        for (int i = 0; i < series.Count; i++)
        {
            if (i < Period - 1)
            {
                _values.Add(null);
            }
            else
            {
                decimal weightedSum = 0;
                bool valid = true;
                for (int j = 0; j < Period; j++)
                {
                    var val = series[i - j];
                    if (!val.HasValue)
                    {
                        valid = false;
                        break;
                    }
                    weightedSum += val.Value * (Period - j);
                }
                _values.Add(valid && weightSum > 0 ? weightedSum / weightSum : null);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
