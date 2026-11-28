using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.OBV)]
    public class CoreObvIndicator : CoreIndicatorBase
    {
        public override string Name => "OBV";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            return CalculateInternal(priceSeries, i => candles[i].Volume);
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            return CalculateInternal(series, _ => 0L);
        }

        private IIndicatorResult CalculateInternal(IReadOnlyList<decimal?> priceSeries, System.Func<int, long> getVolume)
        {
            _values.Clear();
            if (priceSeries == null || priceSeries.Count == 0) return IndicatorResult.Success(_values);

            decimal obv = 0m;
            _values.Add(priceSeries[0].HasValue ? obv : null);

            for (int i = 1; i < priceSeries.Count; i++)
            {
                decimal? curr = priceSeries[i];
                decimal? prev = priceSeries[i - 1];
                if (!curr.HasValue || !prev.HasValue)
                {
                    _values.Add(null);
                    continue;
                }

                long volume = getVolume(i);
                if (curr.Value > prev.Value)
                    obv += volume;
                else if (curr.Value < prev.Value)
                    obv -= volume;
                _values.Add(obv);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
