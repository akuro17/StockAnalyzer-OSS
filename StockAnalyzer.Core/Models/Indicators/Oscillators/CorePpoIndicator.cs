using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.PPO)]
    public class CorePpoIndicator : CoreIndicatorBase
    {
        public int FastPeriod { get; set; } = 12;
        public int SlowPeriod { get; set; } = 26;
        public override string Name => $"PPO ({FastPeriod}, {SlowPeriod})";

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
            if (candles.Count < SlowPeriod)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            decimal fastMult = 2m / (FastPeriod + 1);
            decimal slowMult = 2m / (SlowPeriod + 1);
            decimal? fastEma = null, slowEma = null;

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            var fastEmas = new List<decimal?>();

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < FastPeriod - 1) { fastEmas.Add(null); }
                else if (fastEma == null) { fastEma = priceSeries.Take(FastPeriod).Average(v => v ?? 0m); fastEmas.Add(fastEma); }
                else { fastEma = ((priceSeries[i] ?? 0m) - fastEma.Value) * fastMult + fastEma.Value; fastEmas.Add(fastEma); }
            }

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < SlowPeriod - 1 || !fastEmas[i].HasValue) { _values.Add(null); continue; }

                if (slowEma == null) { slowEma = priceSeries.Take(SlowPeriod).Average(v => v ?? 0m); }
                else { slowEma = ((priceSeries[i] ?? 0m) - slowEma!.Value) * slowMult + slowEma.Value; }

                if(slowEma!.Value != 0)
                {
                    _values.Add(((fastEmas[i]!.Value - slowEma.Value) / slowEma.Value) * 100);
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
