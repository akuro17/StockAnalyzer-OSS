using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.DPO)]
    public class CoreDpoIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 20;
        public override string Name => $"DPO ({Period})";

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
            int shift = Period / 2 + 1;

            // Calculate SMA first
            var sma = new List<decimal?>();
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1) { sma.Add(null); continue; }
                decimal sum = 0;
                for (int j = 0; j < Period; j++) sum += candles[i - j].Close;
                sma.Add(sum / Period);
            }

            // DPO = Close - SMA from `shift` periods in the past
            // DPO(i) = Close(i) - SMA(i - shift)
            for (int i = 0; i < candles.Count; i++)
            {
                int smaIdx = i - shift;
                if (smaIdx < 0 || smaIdx >= sma.Count || !sma[smaIdx].HasValue)
                {
                    _values.Add(null);
                    continue;
                }
                _values.Add(candles[i].Close - sma[smaIdx]!.Value);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
