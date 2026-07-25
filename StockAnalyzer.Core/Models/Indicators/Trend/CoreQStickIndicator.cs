using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.QStick)]
    public class CoreQStickIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 8;
        public override string Name => $"QStick ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1) { _values.Add(null); continue; }
                decimal sum = 0;
                for (int j = 0; j < Period; j++) sum += candles[i - j].Close - candles[i - j].Open;
                _values.Add(sum / Period);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
