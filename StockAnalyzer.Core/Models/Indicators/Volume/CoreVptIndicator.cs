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

            if (!candles.Any())
            {
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            decimal previousVpt = 0;
            // The first value is typically 0 or based on an initial value not provided here.
            _values.Add(0);

            for (int i = 1; i < candles.Count; i++)
            {
                var currentCandle = candles[i];
                decimal previousPrice = priceSeries[i - 1] ?? 0m;

                if (previousPrice == 0)
                {
                    _values.Add(previousVpt); // Or handle as an error/special case
                    continue;
                }

                decimal priceChangePercent = ((priceSeries[i] ?? 0m) - previousPrice) / previousPrice;
                decimal vpt = previousVpt + currentCandle.Volume * priceChangePercent;
                _values.Add(vpt);
                previousVpt = vpt;
            }

            return IndicatorResult.Success(_values);
        }
    }
}
