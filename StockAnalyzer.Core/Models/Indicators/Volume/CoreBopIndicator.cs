using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.BOP)]
    public class CoreBopIndicator : CoreIndicatorBase
    {
        public override string Name => "BOP";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {


            foreach (var c in candles)
            {
                decimal range = c.High - c.Low;
                if (range == 0)
                {
                    _values.Add(0);
                }
                else
                {
                    _values.Add((c.Close - c.Open) / range);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
