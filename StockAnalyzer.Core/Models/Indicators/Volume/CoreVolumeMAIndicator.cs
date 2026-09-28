using StockAnalyzer.Core.Models.Parameters;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.VolumeMA)]
    public class CoreVolumeMAIndicator : CoreIndicatorBase
    {
        public override string Name => "Volume Moving Average";

        public int Period { get; set; } = 20;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            if (candles.Count < Period)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            long sum = 0;
            for (int i = 0; i < Period; i++)
            {
                sum += candles[i].Volume;
                _values.Add(null);
            }
            _values[_values.Count - 1] = (decimal)sum / Period;


            for (int i = Period; i < candles.Count; i++)
            {
                sum = sum - candles[i - Period].Volume + candles[i].Volume;
                _values.Add((decimal)sum / Period);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
