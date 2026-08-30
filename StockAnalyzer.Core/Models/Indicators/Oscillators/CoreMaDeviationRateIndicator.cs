using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.MaDeviationRate)]
    public class CoreMaDeviationRateIndicator : CoreIndicatorBase
    {
        public override string Name => "MA Deviation Rate";

        public int Period { get; set; } = 25;

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
            decimal sum = 0;
            for (int i = 0; i < candles.Count; i++)
            {
                sum += priceSeries[i] ?? 0m;
                if (i >= Period)
                {
                    sum -= priceSeries[i - Period] ?? 0m;
                    var sma = sum / Period;
                    if (sma == 0)
                    {
                        _values.Add(null);
                    }
                    else
                    {
                        var deviation = ((priceSeries[i] ?? 0m) - sma) / sma * 100;
                        _values.Add(deviation);
                    }
                }
                else
                {
                    _values.Add(null);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
