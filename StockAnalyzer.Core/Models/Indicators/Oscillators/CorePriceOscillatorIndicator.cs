using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Indicators.MovingAverages;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.PriceOscillator)]
    public class CorePriceOscillatorIndicator : CoreIndicatorBase
    {
        public int FastPeriod { get; set; } = IndicatorDefaultConstants.PriceOscillatorFastPeriod;
        public int SlowPeriod { get; set; } = IndicatorDefaultConstants.PriceOscillatorSlowPeriod;
        public override string Name => $"PO ({FastPeriod},{SlowPeriod})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CorePpoParameter p)
            {
                FastPeriod = p.FastPeriod;
                SlowPeriod = p.SlowPeriod;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < SlowPeriod) { for (int i = 0; i < candles.Count; i++) _values.Add(null); return IndicatorResult.Success(_values); }

            var fastSma = new CoreSmaIndicator { Period = FastPeriod, PriceSource = PriceSource };
            var slowSma = new CoreSmaIndicator { Period = SlowPeriod, PriceSource = PriceSource };
            fastSma.Calculate(candles); slowSma.Calculate(candles);

            for (int i = 0; i < candles.Count; i++)
            {
                if (!fastSma.Values[i].HasValue || !slowSma.Values[i].HasValue || slowSma.Values[i] == 0)
                    _values.Add(null);
                else
                    _values.Add((fastSma.Values[i]!.Value - slowSma.Values[i]!.Value) / slowSma.Values[i]!.Value * 100);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
