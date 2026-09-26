using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.SMMA)]
public class CoreSmmaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"SMMA ({Period})";

    public override void Configure(CoreIndicatorParameterBase parameters)
    {
        if (parameters is CoreSmaParameter p)
        {
            Period = p.Period;
        }
    }

    protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
    {
        _values.Clear();
        if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

        decimal? smma = null;
        for (int i = 0; i < series.Count; i++)
        {
            if (i < Period - 1 || !series[i].HasValue) { _values.Add(null); continue; }

            if (smma == null)
            {
                decimal sum = 0;
                bool valid = true;
                for (int j = 0; j < Period; j++)
                {
                    var val = series[i - j];
                    if (!val.HasValue) { valid = false; break; }
                    sum += val.Value;
                }
                smma = valid ? sum / Period : null;
            }
            else
            {
                smma = (smma.Value * (Period - 1) + series[i]!.Value) / Period;
            }
            _values.Add(smma);
        }

        return IndicatorResult.Success(_values);
    }
}
