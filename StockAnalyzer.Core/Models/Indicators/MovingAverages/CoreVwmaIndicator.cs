using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages
{
    [StockAnalyzerIndicator(IndicatorType.VWMA)]
    public class CoreVwmaIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 20;
        public override string Name => $"VWMA ({Period})";

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

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            for (int i = 0; i < candles.Count; i++)
            {
                if (Period <= 0 || i < Period - 1) { _values.Add(null); continue; }

                decimal sumPV = 0m, sumV = 0m;
                bool hasNull = false;
                for (int j = 0; j < Period; j++)
                {
                    decimal? price = priceSeries[i - j];
                    if (!price.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    sumPV += price.Value * candles[i - j].Volume;
                    sumV += candles[i - j].Volume;
                }
                _values.Add(hasNull || sumV == 0m ? null : sumPV / sumV);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
