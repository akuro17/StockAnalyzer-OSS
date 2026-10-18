using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;

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
            _values.Clear();
            if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period || candles[i - Period].Volume == 0)
                {
                    _values.Add(null);
                    continue;
                }

                decimal prev = candles[i - Period].Volume;
                decimal curr = candles[i].Volume;
                _values.Add(((curr - prev) / prev) * 100m);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
