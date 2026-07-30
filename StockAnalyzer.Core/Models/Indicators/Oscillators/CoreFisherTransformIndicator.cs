using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Math = System.Math;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.FisherTransform)]
    public class CoreFisherTransformIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 10;
        public override string Name => $"Fisher ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }
        public List<decimal?> Signal { get; } = new();

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
 Signal.Clear();
            var candleDataList = candles.ToList();
            if (candles.Count < Period) { for (int i = 0; i < candles.Count; i++) { _values.Add(null); Signal.Add(null); } return IndicatorResult.Success(_values); }

            decimal prevValue = 0, prevFisher = 0;
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1) { _values.Add(null); Signal.Add(null); continue; }
                decimal high = decimal.MinValue, low = decimal.MaxValue;
                for (int j = 0; j < Period; j++) { high = Math.Max(high, candles[i - j].High); low = Math.Min(low, candles[i - j].Low); }
                decimal mid = (candles[i].High + candles[i].Low) / 2;
                decimal range = high - low;
                decimal v = range == 0 ? 0 : 0.66m * ((mid - low) / range - 0.5m) + 0.67m * prevValue;
                v = Math.Max(-0.999m, Math.Min(0.999m, v));
                decimal fisher = 0.5m * (decimal)Math.Log((double)((1 + v) / (1 - v))) + 0.5m * prevFisher;
                _values.Add(fisher);
                Signal.Add(prevFisher);
                prevValue = v; prevFisher = fisher;
            }

            return IndicatorResult.Success(_values);
        }
    }
}
