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

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1) { _values.Add(null); continue; }

                decimal sumPV = 0, sumV = 0;
                for (int j = 0; j < Period; j++)
                {
                    sumPV += candles[i - j].Close * candles[i - j].Volume;
                    sumV += candles[i - j].Volume;
                }
                _values.Add(sumV == 0 ? null : sumPV / sumV);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
