using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.OBV)]
    public class CoreObvIndicator : CoreIndicatorBase
    {
        public override string Name => "OBV";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            decimal obv = 0;
            _values.Add(obv);

            for (int i = 1; i < candles.Count; i++)
            {
                if (candles[i].Close > candles[i - 1].Close)
                    obv += candles[i].Volume;
                else if (candles[i].Close < candles[i - 1].Close)
                    obv -= candles[i].Volume;
                _values.Add(obv);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
