using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    public enum PivotPointType { Standard, Fibonacci, Woodie, Camarilla }

    public class CorePivotPointsAdvancedIndicator : CoreIndicatorBase
    {
        public override string Name => "Pivot Points Advanced";

        public PivotPointType PivotType { get; set; } = PivotPointType.Standard;

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
                var open = prevCandle.Open;
                var range = high - low;

                decimal pivot = 0;

                switch (PivotType)
                {
                    case PivotPointType.Standard:
                        pivot = (high + low + close) / 3;
                        break;
                    case PivotPointType.Fibonacci:
                        pivot = (high + low + close) / 3;
                        break;
                    case PivotPointType.Woodie:
                        pivot = (high + low + 2 * open) / 4;
                        break;
                    case PivotPointType.Camarilla:
                        pivot = (high + low + close) / 3;
                        break;
                }
                _values.Add(pivot);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
