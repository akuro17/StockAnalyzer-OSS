using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.Volume)]
    public class CoreVolumeIndicator : CoreIndicatorBase
    {
        public override string Name => "Volume";

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count == 0)
            {
                return IndicatorResult.Success(_values);
            }

            for (int i = 0; i < candles.Count; i++)
            {
                _values.Add((decimal)candles[i].Volume);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
