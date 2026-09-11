using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.EOM)]
    public class CoreEaseOfMovementIndicator : CoreIndicatorBase
    {
        public override bool IsOverlay => false; // EMV oscillates around 0, should be in sub-chart
        
        public int Period { get; set; } = 14;
        public override string Name => $"EMV ({Period})";

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
            var emv = new List<decimal> { 0 };
            for (int i = 1; i < candles.Count; i++)
            {
                decimal dm = ((candles[i].High + candles[i].Low) / 2) - ((candles[i - 1].High + candles[i - 1].Low) / 2);
                decimal boxRatio = candles[i].Volume / (candles[i].High - candles[i].Low == 0 ? 1 : (candles[i].High - candles[i].Low));
                emv.Add(boxRatio == 0 ? 0 : dm / boxRatio * 10000000);
            }

            // SMA of EMV
            for (int i = 0; i < emv.Count; i++)
            {
                if (i < Period) { _values.Add(null); continue; } // EMV candles has one less "real" item, so i < Period works
                _values.Add(emv.Skip(i - Period + 1).Take(Period).Average());
            }

            return IndicatorResult.Success(_values);
        }
    }
}
