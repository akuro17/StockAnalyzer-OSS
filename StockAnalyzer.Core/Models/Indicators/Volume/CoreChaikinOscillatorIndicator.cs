using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.ChaikinOscillator)]
    public class CoreChaikinOscillatorIndicator : CoreIndicatorBase
    {
        public int FastPeriod { get; set; } = 3;
        public int SlowPeriod { get; set; } = 10;
        public override string Name => $"Chaikin ({FastPeriod}, {SlowPeriod})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreChaikinOscillatorParameter p)
            {
                FastPeriod = p.FastPeriod;
                SlowPeriod = p.SlowPeriod;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _values.Clear();
            if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

            int minRequired = Math.Max(FastPeriod, SlowPeriod);
            if (candles.Count < minRequired || minRequired <= 0)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            // Calculate ADL first
            var adl = new List<decimal?>(candles.Count);
            decimal adlValue = 0m;
            foreach (var c in candles)
            {
                decimal range = c.High - c.Low;
                decimal mfm = range == 0m ? 0m : ((c.Close - c.Low) - (c.High - c.Close)) / range;
                adlValue += mfm * c.Volume;
                adl.Add(adlValue);
            }

            // EMA of ADL
            var fastEma = IndicatorCalculationHelper.CalculateEmaWithNulls(adl, FastPeriod);
            var slowEma = IndicatorCalculationHelper.CalculateEmaWithNulls(adl, SlowPeriod);

            for (int i = 0; i < candles.Count; i++)
            {
                if (fastEma[i].HasValue && slowEma[i].HasValue)
                {
                    _values.Add(fastEma[i]!.Value - slowEma[i]!.Value);
                }
                else
                {
                    _values.Add(null);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
