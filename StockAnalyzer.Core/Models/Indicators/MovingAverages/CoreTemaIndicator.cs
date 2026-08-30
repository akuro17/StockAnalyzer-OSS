using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages;

[StockAnalyzerIndicator(IndicatorType.TEMA)]
public class CoreTemaIndicator : CoreIndicatorBase
{
    public int Period { get; set; } = 20;
    public override string Name => $"TEMA ({Period})";

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
        if (series == null || series.Count < 3 * Period - 2)
        {
            if (series != null)
            {
                for (int i = 0; i < series.Count; i++) _values.Add(null);
            }
            return IndicatorResult.Success(_values);
        }

        decimal mult = 2m / (Period + 1);

        var ema1 = new List<decimal?>(series.Count);
        var ema2 = new List<decimal?>(series.Count);
        var ema3 = new List<decimal?>(series.Count);

        decimal? e1 = null, e2 = null, e3 = null;

        for (int i = 0; i < series.Count; i++)
        {
            if (i < Period - 1 || !series[i].HasValue) { ema1.Add(null); continue; }
            if (e1 == null)
            {
                decimal sum = 0;
                bool valid = true;
                for (int j = 0; j < Period; j++)
                {
                    var val = series[i - j];
                    if (!val.HasValue) { valid = false; break; }
                    sum += val.Value;
                }
                if (valid) e1 = sum / Period;
            }
            else
            {
                e1 = (series[i]!.Value - e1.Value) * mult + e1.Value;
            }
            ema1.Add(e1);
        }

        for (int i = 0; i < ema1.Count; i++)
        {
            if (!ema1[i].HasValue || i < 2 * Period - 2) { ema2.Add(null); continue; }

            if (e2 == null)
            {
                decimal sum = 0;
                bool valid = true;
                for (int j = 0; j < Period; j++)
                {
                    var val = ema1[i - j];
                    if (!val.HasValue) { valid = false; break; }
                    sum += val.Value;
                }
                if (valid) e2 = sum / Period;
            }
            else
            {
                e2 = (ema1[i]!.Value - e2.Value) * mult + e2.Value;
            }
            ema2.Add(e2);
        }

        for (int i = 0; i < ema2.Count; i++)
        {
            if (!ema2[i].HasValue || i < 3 * Period - 3) { ema3.Add(null); continue; }

            if (e3 == null)
            {
                decimal sum = 0;
                bool valid = true;
                for (int j = 0; j < Period; j++)
                {
                    var val = ema2[i - j];
                    if (!val.HasValue) { valid = false; break; }
                    sum += val.Value;
                }
                if (valid) e3 = sum / Period;
            }
            else
            {
                e3 = (ema2[i]!.Value - e3.Value) * mult + e3.Value;
            }
            ema3.Add(e3);
        }

        for (int i = 0; i < series.Count; i++)
        {
            if (i < 3 * Period - 3 || !ema1[i].HasValue || !ema2[i].HasValue || !ema3[i].HasValue)
            {
                _values.Add(null);
            }
            else
            {
                _values.Add(3 * ema1[i]!.Value - 3 * ema2[i]!.Value + ema3[i]!.Value);
            }
        }

        return IndicatorResult.Success(_values);
    }
}
