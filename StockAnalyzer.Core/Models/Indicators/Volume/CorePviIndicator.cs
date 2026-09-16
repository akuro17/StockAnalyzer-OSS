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
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            decimal pvi = 1000;
            _values.Add(pvi);
            for (int i = 1; i < candles.Count; i++)
            {
                if (candles[i - 1].Volume > 0 && candles[i].Volume > candles[i - 1].Volume)
                {
                    decimal prevPrice = priceSeries[i - 1] ?? 0m;
                    if (prevPrice != 0)
                        pvi = pvi * (1 + ((priceSeries[i] ?? 0m) - prevPrice) / prevPrice);
                }
                _values.Add(pvi);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
