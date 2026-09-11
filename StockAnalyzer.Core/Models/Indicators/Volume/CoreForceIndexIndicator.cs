using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.ForceIndex)]
    public class CoreForceIndexIndicator : CoreIndicatorBase
    {
        public override string Name => "Force Index";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < 2)
            {
                if (candles.Count == 1) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            _values.Add(null);
            for (int i = 1; i < candles.Count; i++)
                _values.Add(((priceSeries[i] ?? 0m) - (priceSeries[i - 1] ?? 0m)) * candles[i].Volume);

            return IndicatorResult.Success(_values);
        }
    }
}
