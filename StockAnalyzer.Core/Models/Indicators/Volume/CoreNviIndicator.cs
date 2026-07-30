using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.NegativeVolumeIndex)]
    public class CoreNviIndicator : CoreIndicatorBase
    {
        public override string Name => "NVI";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            decimal nvi = 1000;
            _values.Add(nvi);
            for (int i = 1; i < candles.Count; i++)
            {
                if (candles[i - 1].Volume > 0 && candles[i].Volume < candles[i - 1].Volume)
                {
                    if (candles[i - 1].Close != 0)
                        nvi = nvi * (1 + (candles[i].Close - candles[i - 1].Close) / candles[i - 1].Close);
                }
                _values.Add(nvi);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
