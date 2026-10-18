using System.Collections.Generic;
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
            _values.Clear();
            if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

            if (candles.Count < Period || Period <= 0)
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
                if (i < Period)
                {
                    _values.Add(null);
                    continue;
                }

                int upwardDays = 0;
                bool hasNull = false;
                for (int j = i - Period + 1; j <= i; j++)
                {
                    if (!priceSeries[j].HasValue || !priceSeries[j - 1].HasValue)
                    {
                        hasNull = true;
                        break;
                    }

                    if (priceSeries[j]!.Value > priceSeries[j - 1]!.Value)
                    {
                        upwardDays++;
                    }
                }

                if (hasNull)
                {
                    _values.Add(null);
                }
                else
                {
                    _values.Add(100m * upwardDays / Period);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
