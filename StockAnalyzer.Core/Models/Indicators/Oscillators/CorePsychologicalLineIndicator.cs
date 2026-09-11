using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.PsychologicalLine)]
    public class CorePsychologicalLineIndicator : CoreIndicatorBase
    {
        public override string Name => "Psychological Line";

        public int Period { get; set; } = 12;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            if (candles.Count < Period)
            {
                for (int i = 0; i < candles.Count; i++)
                {
                    _values.Add(null);
                }
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                int upwardDays = 0;
                for (int j = i - Period + 1; j <= i; j++)
                {
                    if (j > 0 && (priceSeries[j] ?? 0m) > (priceSeries[j - 1] ?? 0m))
                    {
                        upwardDays++;
                    }
                }
                _values.Add(100m * upwardDays / Period);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
