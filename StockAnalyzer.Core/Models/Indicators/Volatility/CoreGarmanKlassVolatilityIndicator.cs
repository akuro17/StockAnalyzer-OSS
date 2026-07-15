using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.GarmanKlassVolatility)]
    public class CoreGarmanKlassVolatilityIndicator : CoreIndicatorBase
    {
        public override string Name => "Garman-Klass Volatility";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters to configure
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            var factor = 2.0 * Math.Log(2.0) - 1.0;

            foreach (var candle in candles)
            {
                if (candle.Low <= 0 || candle.Open <= 0)
                {
                    _values.Add(null);
                    continue;
                }

                var h_l = Math.Log((double)(candle.High / candle.Low));
                var c_o = Math.Log((double)(candle.Close / candle.Open));

                var variance = 0.5 * h_l * h_l - factor * c_o * c_o;

                if (variance < 0)
                {
                    _values.Add(null);
                }
                else
                {
                    var volatility = (decimal)Math.Sqrt(variance);
                    _values.Add(volatility);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
