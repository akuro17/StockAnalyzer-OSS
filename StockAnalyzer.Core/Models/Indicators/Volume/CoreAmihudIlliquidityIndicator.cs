using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    [StockAnalyzerIndicator(IndicatorType.AmihudIlliquidity)]
    public class CoreAmihudIlliquidityIndicator : CoreIndicatorBase
    {
        public override string Name => $"Amihud Illiquidity({Period})";
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

            if (candles.Count < 2)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var illiquidityValues = new List<decimal?>();
            illiquidityValues.Add(null); // No return for the first candle

            for (int i = 1; i < candles.Count; i++)
            {
                var current = candles[i];
                var previous = candles[i - 1];

                if (previous.Close == 0 || current.Volume == 0)
                {
                    illiquidityValues.Add(0); // Avoid division by zero, treat as perfectly liquid
                    continue;
                }

                decimal returnVal = (current.Close - previous.Close) / previous.Close;
                illiquidityValues.Add(Math.Abs(returnVal) / current.Volume);
            }

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period) // Need at least Period days of illiquidity values, and the first is always null.
                {
                    _values.Add(null);
                    continue;
                }

                decimal? sum = 0;
                int count = 0;
                for (int j = i - Period + 1; j <= i; j++)
                {
                    if (illiquidityValues[j].HasValue)
                    {
                        sum += illiquidityValues[j]!.Value;
                        count++;
                    }
                }
                _values.Add(count > 0 ? sum / count : null);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
