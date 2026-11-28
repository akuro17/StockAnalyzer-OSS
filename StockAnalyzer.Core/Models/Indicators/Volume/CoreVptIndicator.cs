using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.VPT)]
    public class CoreVptIndicator : CoreIndicatorBase
    {
        public override string Name => "VPT";
        public override bool IsOverlay => false;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _values.Clear();
            if (!candles.Any())
            {
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            decimal previousVpt = 0m;
            _values.Add(priceSeries[0].HasValue ? 0m : null);

            for (int i = 1; i < candles.Count; i++)
            {
                decimal? curr = priceSeries[i];
                decimal? prev = priceSeries[i - 1];
                if (!curr.HasValue || !prev.HasValue)
                {
                    _values.Add(null);
                    continue;
                }

                var currentCandle = candles[i];
                decimal previousPrice = prev.Value;

                if (previousPrice == 0m)
                {
                    _values.Add(previousVpt);
                    continue;
                }

                decimal priceChangePercent = (curr.Value - previousPrice) / previousPrice;
                decimal vpt = previousVpt + currentCandle.Volume * priceChangePercent;
                _values.Add(vpt);
                previousVpt = vpt;
            }

            return IndicatorResult.Success(_values);
        }
    }
}
