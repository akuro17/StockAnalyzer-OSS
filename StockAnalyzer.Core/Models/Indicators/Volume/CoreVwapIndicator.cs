using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.VWAP)]
    public class CoreVwapIndicator : CoreIndicatorBase
    {
        public override string Name => "VWAP";
        public override bool IsOverlay => true;

        /// <summary>
        /// Price source used to compute the VWAP numerator.
        /// Defaults to Typical Price ((High + Low + Close) / 3), the industry-standard VWAP source.
        /// </summary>
        public override PriceType PriceSource { get; set; } = PriceType.Typical;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

            decimal cumulativePV = 0;
            decimal cumulativeVolume = 0;

            for (int i = 0; i < candles.Count; i++)
            {
                decimal price = priceSeries[i] ?? 0m;
                cumulativePV += price * candles[i].Volume;
                cumulativeVolume += candles[i].Volume;
                _values.Add(cumulativeVolume == 0 ? null : cumulativePV / cumulativeVolume);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
