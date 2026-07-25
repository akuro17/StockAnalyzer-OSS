using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages
{
    [StockAnalyzerIndicator(IndicatorType.ZLEMA)]
    public class CoreZlemaIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 20;
        public override string Name => $"ZLEMA ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            int lag = (Period - 1) / 2;
            decimal mult = 2m / (Period + 1);

            var emaData = new List<decimal>();
            for(int i = 0; i < candles.Count; i++)
            {
                if (i < lag)
                {
                    emaData.Add(candles[i].Close);
                }
                else
                {
                    emaData.Add(2 * candles[i].Close - candles[i - lag].Close);
                }
            }

            decimal? zlema = null;
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                if (zlema == null)
                {
                    decimal sum = 0;
                    for(int j = 0; j < Period; j++)
                    {
                        sum += emaData[i - j];
                    }
                    zlema = sum / Period;
                }
                else
                {
                    zlema = (emaData[i] - zlema.Value) * mult + zlema.Value;
                }
                _values.Add(zlema);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
