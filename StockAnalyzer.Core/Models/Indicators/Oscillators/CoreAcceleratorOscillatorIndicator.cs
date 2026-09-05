using StockAnalyzer.Core.Models;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.AccelerationOscillator)]
    public class CoreAcceleratorOscillatorIndicator : CoreIndicatorBase
    {
        public int FastPeriod { get; set; } = 5;
        public int SlowPeriod { get; set; } = 34;
        public int SmoothPeriod { get; set; } = 5;
        public override string Name => $"AC ({FastPeriod},{SlowPeriod},{SmoothPeriod})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreAcceleratorOscillatorParameter p)
            {
                FastPeriod = p.FastPeriod;
                SlowPeriod = p.SlowPeriod;
                SmoothPeriod = p.SmoothPeriod;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            var ao = new CoreAwesomeOscillatorIndicator { FastPeriod = FastPeriod, SlowPeriod = SlowPeriod };
            ao.Calculate(candles);

            // SMA of AO
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < SlowPeriod + SmoothPeriod - 2 || !ao.Values[i].HasValue) { _values.Add(null); continue; }
                decimal sum = 0; int count = 0;
                for (int j = 0; j < SmoothPeriod; j++)
                    if (ao.Values[i - j].HasValue) { sum += ao.Values[i - j]!.Value; count++; }
                if (count == 0) { _values.Add(null); continue; }
                _values.Add(ao.Values[i]!.Value - sum / count);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
