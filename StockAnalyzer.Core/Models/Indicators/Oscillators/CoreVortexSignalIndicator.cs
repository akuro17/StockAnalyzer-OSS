using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.VortexSignal)]
    public class CoreVortexSignalIndicator : CoreIndicatorBase
    {
        public override string Name => "Vortex Signal";

        public int Period { get; set; } = 14;

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
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var tr = new List<decimal>();
            var pvm = new List<decimal>();
            var nvm = new List<decimal>();

            for (int i = 1; i < candles.Count; i++)
            {
                var high = candles[i].High;
                var low = candles[i].Low;
                var prevHigh = candles[i - 1].High;
                var prevLow = candles[i - 1].Low;
                var prevClose = candles[i - 1].Close;

                tr.Add(Math.Max(high, prevClose) - Math.Min(low, prevClose));
                pvm.Add(Math.Abs(high - prevLow));
                nvm.Add(Math.Abs(low - prevHigh));
            }

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period)
                {
                    _values.Add(null);
                    continue;
                }

                decimal sumTr = 0;
                decimal sumPvm = 0;
                decimal sumNvm = 0;

                for (int j = i - Period; j < i; j++)
                {
                    int trIndex = j - 1;
                    if (trIndex >= 0 && trIndex < tr.Count)
                    {
                        sumTr += tr[trIndex];
                        sumPvm += pvm[trIndex];
                        sumNvm += nvm[trIndex];
                    }
                }

                if (sumTr == 0)
                {
                    _values.Add(null);
                    continue;
                }

                var pvi = sumPvm / sumTr;
                var nvi = sumNvm / sumTr;

                _values.Add(pvi > nvi ? 1 : -1);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
