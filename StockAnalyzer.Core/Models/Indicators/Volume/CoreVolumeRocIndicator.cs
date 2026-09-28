using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.VolumeROC)]
    public class CoreVolumeRocIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 14;
        public override string Name => $"VROC ({Period})";

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
                if (i < Period || candles[i - Period].Volume == 0) { _values.Add(null); continue; }
                _values.Add((candles[i].Volume - candles[i - Period].Volume) / candles[i - Period].Volume * 100);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
