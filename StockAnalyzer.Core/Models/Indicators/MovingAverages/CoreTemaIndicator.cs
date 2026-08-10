using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages
{
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

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < 3 * Period - 2)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }
            decimal mult = 2m / (Period + 1);

            var ema1 = new List<decimal?>();
            var ema2 = new List<decimal?>();
            var ema3 = new List<decimal?>();

            decimal? e1 = null, e2 = null, e3 = null;

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1) { ema1.Add(null); continue; }
                if (e1 == null) { e1 = candles.Take(Period).Average(c => c.Close); }
                else { e1 = (candles[i].Close - e1!.Value) * mult + e1!.Value; }
                ema1.Add(e1);
            }

            var ema1Values = ema1.Where(v => v.HasValue).Select(v => v!.Value).ToList();
            for (int i = 0; i < ema1.Count; i++)
            {
                 if (!ema1[i].HasValue) { ema2.Add(null); continue; }
                 if (i < 2 * Period - 2) { ema2.Add(null); continue; }

                 if (e2 == null) { e2 = ema1.Where(v => v.HasValue).Take(Period).Average(v => v!.Value); }
                 else { e2 = (ema1[i]!.Value - e2!.Value) * mult + e2!.Value; }
                 ema2.Add(e2);
            }

            for (int i = 0; i < ema2.Count; i++)
            {
                if (!ema2[i].HasValue) { ema3.Add(null); continue; }
                if (i < 3 * Period - 3) { ema3.Add(null); continue; }

                if (e3 == null) { e3 = ema2.Where(v => v.HasValue).Take(Period).Average(v => v!.Value); }
                else { e3 = (ema2[i]!.Value - e3!.Value) * mult + e3!.Value; }
                ema3.Add(e3);
            }

            for(int i = 0; i < candles.Count; i++)
            {
                if(i < 3 * Period - 3 || !ema1[i].HasValue || !ema2[i].HasValue || !ema3[i].HasValue)
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
}
