using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.UltimateOscillator)]
    public class CoreUltimateOscillatorIndicator : CoreIndicatorBase
    {
        public int Period1 { get; set; } = 7;
        public int Period2 { get; set; } = 14;
        public int Period3 { get; set; } = 28;
        public override string Name => $"UO ({Period1}, {Period2}, {Period3})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreUltimateOscillatorParameter p)
            {
                Period1 = p.Period1;
                Period2 = p.Period2;
                Period3 = p.Period3;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < 2) return IndicatorResult.Success(_values);
            var bp = new List<decimal>();
            var tr = new List<decimal>();

            for (int i = 1; i < candles.Count; i++)
            {
                decimal low = Math.Min(candles[i].Low, candles[i - 1].Close);
                decimal high = Math.Max(candles[i].High, candles[i - 1].Close);
                bp.Add(candles[i].Close - low);
                tr.Add(high - low);
            }

            _values.Add(null); // First value is always null as it needs a prior candle
            for (int i = 0; i < bp.Count; i++)
            {
                if (i < Period3 - 2) { _values.Add(null); continue; }

                decimal sum1Bp = 0, sum1Tr = 0, sum2Bp = 0, sum2Tr = 0, sum3Bp = 0, sum3Tr = 0;

                for (int j = 0; j < Period1; j++) { if(i-j >= 0) { sum1Bp += bp[i - j]; sum1Tr += tr[i - j]; } }
                for (int j = 0; j < Period2; j++) { if(i-j >= 0) { sum2Bp += bp[i - j]; sum2Tr += tr[i - j]; } }
                for (int j = 0; j < Period3; j++) { if(i-j >= 0) { sum3Bp += bp[i - j]; sum3Tr += tr[i - j]; } }

                decimal avg1 = sum1Tr == 0 ? 0 : sum1Bp / sum1Tr;
                decimal avg2 = sum2Tr == 0 ? 0 : sum2Bp / sum2Tr;
                decimal avg3 = sum3Tr == 0 ? 0 : sum3Bp / sum3Tr;

                _values.Add(100 * (4 * avg1 + 2 * avg2 + avg3) / 7);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
