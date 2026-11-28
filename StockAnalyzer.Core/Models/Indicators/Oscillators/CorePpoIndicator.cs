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

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            if (FastPeriod <= 0 || SlowPeriod <= 0 || series.Count < SlowPeriod)
            {
                for (int i = 0; i < series.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var fastEmas = IndicatorCalculationHelper.CalculateEmaWithNulls(series, FastPeriod);
            var slowEmas = IndicatorCalculationHelper.CalculateEmaWithNulls(series, SlowPeriod);

            for (int i = 0; i < series.Count; i++)
            {
                if (fastEmas[i] is { } fast && slowEmas[i] is { } slow && slow != 0m)
                {
                    _values.Add(((fast - slow) / slow) * 100m);
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
