using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;
using System.Linq;

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

            // Calculate ADL first
            var adl = new List<decimal>();
            decimal adlValue = 0;
            foreach (var c in candles)
            {
                decimal range = c.High - c.Low;
                decimal mfm = range == 0 ? 0 : ((c.Close - c.Low) - (c.High - c.Close)) / range;
                adlValue += mfm * c.Volume;
                adl.Add(adlValue);
            }

            // EMA of ADL
            decimal fastMult = 2m / (FastPeriod + 1);
            decimal slowMult = 2m / (SlowPeriod + 1);
            decimal? fastEma = null, slowEma = null;

            for (int i = 0; i < adl.Count; i++)
            {
                if (i < SlowPeriod - 1) { _values.Add(null); continue; }

                if (fastEma == null)
                {
                    fastEma = adl.Skip(i - FastPeriod + 1).Take(FastPeriod).Average();
                    slowEma = adl.Skip(i - SlowPeriod + 1).Take(SlowPeriod).Average();
                }
                else
                {
                    fastEma = (adl[i] - fastEma!.Value) * fastMult + fastEma!.Value;
                    slowEma = (adl[i] - slowEma!.Value) * slowMult + slowEma!.Value;
                }
                _values.Add(fastEma - slowEma);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
