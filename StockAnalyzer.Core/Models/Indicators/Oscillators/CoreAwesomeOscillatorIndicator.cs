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
            int count = candles.Count;
            if (count < SlowPeriod)
            {
                for (int i = 0; i < count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            decimal fastSum = 0;
            decimal slowSum = 0;

            for (int i = 0; i < count; i++)
            {
                decimal mp = (candles[i].High + candles[i].Low) / 2m;
                fastSum += mp;
                slowSum += mp;

                if (i >= FastPeriod)
                {
                    decimal oldMpFast = (candles[i - FastPeriod].High + candles[i - FastPeriod].Low) / 2m;
                    fastSum -= oldMpFast;
                }

                if (i >= SlowPeriod)
                {
                    decimal oldMpSlow = (candles[i - SlowPeriod].High + candles[i - SlowPeriod].Low) / 2m;
                    slowSum -= oldMpSlow;
                }

                if (i < SlowPeriod - 1)
                {
                    _values.Add(null);
                }
                else
                {
                    decimal fastSma = fastSum / FastPeriod;
                    decimal slowSma = slowSum / SlowPeriod;
                    _values.Add(fastSma - slowSma);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
