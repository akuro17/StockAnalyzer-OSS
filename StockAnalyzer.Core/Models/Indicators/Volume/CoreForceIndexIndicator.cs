using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.ForceIndex)]
    public class CoreForceIndexIndicator : CoreIndicatorBase
    {
        public override string Name => "Force Index";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < 2)
            {
                if (candles.Count == 1) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            _values.Add(null);
            for (int i = 1; i < candles.Count; i++)
                _values.Add((candles[i].Close - candles[i - 1].Close) * candles[i].Volume);

            return IndicatorResult.Success(_values);
        }
    }
}
