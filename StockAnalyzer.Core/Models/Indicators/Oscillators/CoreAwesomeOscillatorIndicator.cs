using StockAnalyzer.Core.Models;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.AwesomeOscillator)]
    public class CoreAwesomeOscillatorIndicator : CoreIndicatorBase
    {
        public int FastPeriod { get; set; } = 5;
        public int SlowPeriod { get; set; } = 34;
        public override string Name => $"AO ({FastPeriod},{SlowPeriod})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreAwesomeOscillatorParameter p)
            {
                FastPeriod = p.FastPeriod;
                SlowPeriod = p.SlowPeriod;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            var midpoints = candles.Select(c => (c.High + c.Low) / 2).ToList();

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < SlowPeriod - 1) { _values.Add(null); continue; }
                decimal fastSma = midpoints.Skip(i - FastPeriod + 1).Take(FastPeriod).Average();
                decimal slowSma = midpoints.Skip(i - SlowPeriod + 1).Take(SlowPeriod).Average();
                _values.Add(fastSma - slowSma);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
