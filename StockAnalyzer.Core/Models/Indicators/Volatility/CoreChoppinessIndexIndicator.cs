using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using Math = System.Math;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.ChoppinessIndex)]
    public class CoreChoppinessIndexIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 14;
        public override string Name => $"Choppiness ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            if (Period < 2 || candles.Count < Period + 1)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            double log10Period = Math.Log10(Period);
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period) { _values.Add(null); continue; }

                decimal atrSum = 0;
                decimal highestHigh = decimal.MinValue;
                decimal lowestLow = decimal.MaxValue;

                for (int j = 0; j < Period; j++)
                {
                    int idx = i - j;
                    decimal tr = Math.Max(candles[idx].High - candles[idx].Low,
                                 Math.Max(Math.Abs(candles[idx].High - candles[idx - 1].Close),
                                          Math.Abs(candles[idx].Low - candles[idx - 1].Close)));
                    atrSum += tr;
                    highestHigh = Math.Max(highestHigh, candles[idx].High);
                    lowestLow = Math.Min(lowestLow, candles[idx].Low);
                }

                decimal range = highestHigh - lowestLow;
                if (range == 0 || atrSum == 0)
                {
                    _values.Add(100m); // Max choppiness if no range or ATR
                }
                else
                {
                    double ci = 100.0 * Math.Log10((double)(atrSum / range)) / log10Period;
                    _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(ci));
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
