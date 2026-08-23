using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.VWAP)]
    public class CoreVwapIndicator : CoreIndicatorBase
    {
        public override string Name => "VWAP";
        public override bool IsOverlay => true;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            decimal cumulativeTPV = 0;
            decimal cumulativeVolume = 0;

            foreach (var c in candles)
            {
                decimal tp = (c.High + c.Low + c.Close) / 3;
                cumulativeTPV += tp * c.Volume;
                cumulativeVolume += c.Volume;
                _values.Add(cumulativeVolume == 0 ? null : cumulativeTPV / cumulativeVolume);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
