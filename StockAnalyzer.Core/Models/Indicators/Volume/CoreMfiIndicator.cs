using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.MFI)]
    public class CoreMfiIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 14;
        public override string Name => $"MFI ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < 2) return IndicatorResult.Success(_values);
            _values.Add(null);

            for (int i = 1; i < candles.Count; i++)
            {
                if (i < Period) { _values.Add(null); continue; }

                decimal posFlow = 0, negFlow = 0;
                for (int j = 0; j < Period; j++)
                {
                    int idx = i - j;
                    decimal tp = (candles[idx].High + candles[idx].Low + candles[idx].Close) / 3;
                    decimal prevTp = (candles[idx - 1].High + candles[idx - 1].Low + candles[idx - 1].Close) / 3;
                    decimal mf = tp * candles[idx].Volume;

                    if (tp > prevTp) posFlow += mf;
                    else if (tp < prevTp) negFlow += mf;
                }

                decimal mfi = negFlow == 0 ? 100 : 100 - (100 / (1 + posFlow / negFlow));
                _values.Add(mfi);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
