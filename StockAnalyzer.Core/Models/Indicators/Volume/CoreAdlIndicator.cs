using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.ADL)]
    public class CoreAdlIndicator : CoreIndicatorBase
    {
        public override string Name => "ADL";
        public override bool IsOverlay => false;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            // No parameters
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            decimal adl = 0;
            foreach (var c in candles)
            {
                decimal range = c.High - c.Low;
                decimal mfm = range == 0 ? 0 : ((c.Close - c.Low) - (c.High - c.Close)) / range;
                adl += mfm * c.Volume;
                _values.Add(adl);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
