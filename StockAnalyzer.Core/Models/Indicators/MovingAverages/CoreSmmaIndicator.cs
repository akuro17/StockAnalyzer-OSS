using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages
{
    [StockAnalyzerIndicator(IndicatorType.SMMA)]
    public class CoreSmmaIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 20;
        public override string Name => $"SMMA ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            decimal? smma = null;
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1) { _values.Add(null); continue; }

                if (smma == null)
                {
                    decimal sum = 0;
                    for (int j = 0; j < Period; j++) sum += candles[i - j].Close;
                    smma = sum / Period;
                }
                else
                {
                    smma = (smma.Value * (Period - 1) + candles[i].Close) / Period;
                }
                _values.Add(smma);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
