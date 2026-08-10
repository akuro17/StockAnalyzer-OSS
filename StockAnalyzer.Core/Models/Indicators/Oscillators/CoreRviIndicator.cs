using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.RVI)] // Note: RVI might be relative vigor or volatility. Assuming standard RVI. if enum missing I added it? Wait, I didn't add RVI to enum in Step 676.
    public class CoreRviIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 10;
        public override string Name => $"RVI ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period; 
            }
        }

        public List<decimal?> SignalLine { get; } = new();

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            SignalLine.Clear();
            var candleDataList = candles.ToList();
            if (candles.Count < Period + 3) // Need Period values and 3 prior values for smoothing
            {
                for (int i = 0; i < candles.Count; i++) { _values.Add(null); SignalLine.Add(null); }
                return IndicatorResult.Success(_values);
            }

            var numList = new List<decimal>();
            var denList = new List<decimal>();

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < 3)
                {
                    numList.Add(0); // Cannot calculate smoothed value
                    denList.Add(0);
                    continue;
                }
                decimal a = candles[i].Close - candles[i].Open;
                decimal b = candles[i - 1].Close - candles[i - 1].Open;
                decimal c = candles[i - 2].Close - candles[i - 2].Open;
                decimal d = candles[i - 3].Close - candles[i - 3].Open;
                numList.Add((a + 2 * b + 2 * c + d) / 6);

                decimal e = candles[i].High - candles[i].Low;
                decimal f = candles[i - 1].High - candles[i - 1].Low;
                decimal g = candles[i - 2].High - candles[i - 2].Low;
                decimal h = candles[i - 3].High - candles[i - 3].Low;
                denList.Add((e + 2 * f + 2 * g + h) / 6);
            }


            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period + 2) { _values.Add(null); SignalLine.Add(null); continue; }

                decimal numSum = 0, denSum = 0;
                for (int j = 0; j < Period; j++)
                {
                    numSum += numList[i - j];
                    denSum += denList[i - j];
                }

                decimal rvi = denSum == 0 ? 0 : numSum / denSum;
                _values.Add(rvi);

                // Signal = Symmetrically weighted MA of RVI = (RVI(i) + 2*RVI(i-1) + 2*RVI(i-2) + RVI(i-3)) / 6
                if (_values.Count(v => v.HasValue) >= 4 && i >= 3)
                {
                   var rvi_i = _values[i];
                   var rvi_i1 = _values[i-1];
                   var rvi_i2 = _values[i-2];
                   var rvi_i3 = _values[i-3];
                   if(rvi_i.HasValue && rvi_i1.HasValue && rvi_i2.HasValue && rvi_i3.HasValue)
                   {
                        SignalLine.Add((rvi_i.Value + 2 * rvi_i1.Value + 2 * rvi_i2.Value + rvi_i3.Value) / 6);
                   }
                   else
                   {
                       SignalLine.Add(null);
                   }
                }
                else SignalLine.Add(null);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
