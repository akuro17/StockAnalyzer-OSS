using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.PVI)]
    public class CorePviIndicator : CoreIndicatorBase
    {
        public override string Name => "PVI";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _values.Clear();
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            decimal pvi = 1000m;
            _values.Add(priceSeries[0].HasValue ? pvi : null);
            for (int i = 1; i < candles.Count; i++)
            {
                decimal? curr = priceSeries[i];
                decimal? prev = priceSeries[i - 1];
                if (!curr.HasValue || !prev.HasValue)
                {
                    _values.Add(null);
                    continue;
                }

                if (candles[i - 1].Volume > 0 && candles[i].Volume > candles[i - 1].Volume)
                {
                    decimal prevPrice = prev.Value;
                    if (prevPrice != 0m)
                        pvi = pvi * (1m + (curr.Value - prevPrice) / prevPrice);
                }
                _values.Add(pvi);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
