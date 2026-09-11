using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.MassIndex)]
    public class CoreMassIndexIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 25;
        public int EmaPeriod { get; set; } = 9;

        public override string Name => $"Mass Index ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreMassIndexParameter p)
            {
                Period = p.Period;
                EmaPeriod = p.EmaPeriod;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            // EMA period for Mass Index is typically 9
            int emaPeriod = 9;
            decimal mult = 2m / (emaPeriod + 1);
            decimal? singleEma = null, doubleEma = null;
            var ratios = new List<decimal?>();

            for (int i = 0; i < candles.Count; i++)
            {
                decimal range = candles[i].High - candles[i].Low;

                if (i < emaPeriod - 1)
                {
                    singleEma = null;
                }
                else if (singleEma == null)
                {
                    singleEma = candles.Take(emaPeriod).Average(c => c.High - c.Low);
                }
                else
                {
                    singleEma = (range - singleEma.Value) * mult + singleEma.Value;
                }

                if (i < 2 * emaPeriod - 2)
                {
                     doubleEma = null;
                }
                else if (doubleEma == null)
                {
                    var singleEmas = new List<decimal>();
                    decimal? tempEma = null;
                    for(int k=0; k < emaPeriod*2-1; k++)
                    {
                        if(k >= emaPeriod -1)
                        {
                            if(tempEma == null) tempEma = candles.Take(emaPeriod).Average(c=>c.High-c.Low);
                            else tempEma = ((candles[k].High-candles[k].Low) - tempEma.Value) * mult + tempEma.Value;
                            singleEmas.Add(tempEma.Value);
                        }
                    }
                    doubleEma = singleEmas.Take(emaPeriod).Average();
                }
                else
                {
                    if (singleEma.HasValue)
                        doubleEma = (singleEma.Value - doubleEma.Value) * mult + doubleEma.Value;
                }

                ratios.Add(doubleEma == 0 || !singleEma.HasValue || !doubleEma.HasValue ? null : singleEma.Value / doubleEma.Value);
            }

            for (int i = 0; i < ratios.Count; i++)
            {
                if (i < (2 * emaPeriod - 2) + (Period - 1))
                {
                    _values.Add(null);
                    continue;
                }

                decimal sum = 0;
                bool hasEnoughData = true;
                for (int j = 0; j < Period; j++)
                {
                    if (!ratios[i - j].HasValue)
                    {
                        hasEnoughData = false;
                        break;
                    }
                    sum += ratios[i - j]!.Value;
                }

                _values.Add(hasEnoughData ? sum : null);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
