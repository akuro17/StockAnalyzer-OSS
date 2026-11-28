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
            if (Period < 1 || candles.Count < 2)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            var illiquidityValues = new List<decimal?>(candles.Count);
            illiquidityValues.Add(null); // No return for the first candle

            for (int i = 1; i < candles.Count; i++)
            {
                var current = candles[i];
                decimal? currPriceNullable = priceSeries[i];
                decimal? prevPriceNullable = priceSeries[i - 1];

                if (!currPriceNullable.HasValue || !prevPriceNullable.HasValue ||
                    prevPriceNullable.Value <= 0m || currPriceNullable.Value <= 0m || current.Volume <= 0)
                {
                    illiquidityValues.Add(null);
                    continue;
                }

                decimal currPrice = currPriceNullable.Value;
                decimal prevPrice = prevPriceNullable.Value;
                decimal returnVal = (currPrice - prevPrice) / prevPrice;
                illiquidityValues.Add(Math.Abs(returnVal) / current.Volume);
            }

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period) // Need at least Period days of illiquidity values, and the first is always null.
                {
                    _values.Add(null);
                    continue;
                }

                decimal sum = 0m;
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
