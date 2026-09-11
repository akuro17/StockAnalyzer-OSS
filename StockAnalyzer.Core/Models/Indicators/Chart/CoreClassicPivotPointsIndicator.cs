using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    [StockAnalyzerIndicator(IndicatorType.ClassicPivotPoints)]
    public class CoreClassicPivotPointsIndicator : CoreIndicatorBase
    {
        public override string Name => "Classic Pivot Points";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {


            if (candles.Count < 2)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            _values.Add(null);

            for (int i = 1; i < candles.Count; i++)
            {
                var prevCandle = candles[i - 1];
                var high = prevCandle.High;
                var low = prevCandle.Low;
                var close = prevCandle.Close;

                var pivot = (high + low + close) / 3;
                _values.Add(pivot);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
