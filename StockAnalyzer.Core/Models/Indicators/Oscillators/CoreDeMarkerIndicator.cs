using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.DeMarker)]
    public class CoreDeMarkerIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 14;
        public override string Name => $"DeMarker ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < 2) return IndicatorResult.Success(_values);
            var deMax = new List<decimal>();
            var deMin = new List<decimal>();

            deMax.Add(0); deMin.Add(0);
            for (int i = 1; i < candles.Count; i++)
            {
                decimal high = candles[i].High, prevHigh = candles[i - 1].High;
                decimal low = candles[i].Low, prevLow = candles[i - 1].Low;
                deMax.Add(high > prevHigh ? high - prevHigh : 0);
                deMin.Add(low < prevLow ? prevLow - low : 0);
            }

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period) { _values.Add(null); continue; }
                decimal sumMax = 0, sumMin = 0;
                for (int j = 0; j < Period; j++) { sumMax += deMax[i - j]; sumMin += deMin[i - j]; }
                decimal total = sumMax + sumMin;
                _values.Add(total == 0 ? 0.5m : sumMax / total);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
