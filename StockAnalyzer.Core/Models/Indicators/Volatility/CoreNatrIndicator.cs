using StockAnalyzer.Core.Models;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.NATR)]
    public class CoreNatrIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 14;
        public override string Name => $"NATR ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period; 
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            var atr = new CoreAtrIndicator { Period = Period };
            atr.Calculate(candles);

            for (int i = 0; i < candles.Count; i++)
            {
                if (!atr.Values[i].HasValue || candles[i].Close == 0) { _values.Add(null); continue; }
                _values.Add(atr.Values[i]!.Value / candles[i].Close * 100);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
