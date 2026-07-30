using StockAnalyzer.Core.Models.Indicators;
using StockAnalyzer.Core.Models.Parameters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.MovingAverages
{
    [StockAnalyzerIndicator(IndicatorType.HMA)]
    public class CoreHmaIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 20;
        public override string Name => $"HMA ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter param)
            {
                Period = param.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count == 0) return IndicatorResult.Success(_values);
            int halfPeriod = Period / 2;
            int sqrtPeriod = (int)Math.Sqrt(Period);

            var wma1 = CalculateWma(candles, halfPeriod);
            var wma2 = CalculateWma(candles, Period);

            var diff = new List<decimal?>();
            for (int i = 0; i < candles.Count; i++)
            {
                if (wma1[i].HasValue && wma2[i].HasValue)
                    diff.Add(2 * wma1[i]!.Value - wma2[i]!.Value);
                else
                    diff.Add(null);
            }

            // WMA of diff
            decimal weightSum = (decimal)sqrtPeriod * (sqrtPeriod + 1) / 2m;
            for (int i = 0; i < diff.Count; i++)
            {
                // The first value of diff is at Period-1. WMA needs sqrtPeriod values. So first HMA is at (Period-1) + (sqrtPeriod-1).
                if (i < (Period - 1) + (sqrtPeriod - 1))
                {
                    _values.Add(null);
                    continue;
                }

                decimal sum = 0;
                bool canCalculate = true;
                for (int j = 0; j < sqrtPeriod; j++)
                {
                    if (!diff[i - j].HasValue)
                    {
                        canCalculate = false;
                        break;
                    }
                    sum += diff[i - j]!.Value * (sqrtPeriod - j);
                }

                if (canCalculate && weightSum > 0)
                {
                    _values.Add(sum / weightSum);
                }
                else
                {
                    _values.Add(null);
                }
            }

            return IndicatorResult.Success(_values);
        }

        private List<decimal?> CalculateWma(IReadOnlyList<CoreCandleData> candles, int period)
        {
            var result = new List<decimal?>();
            if (period <= 0)
            {
                result.AddRange(Enumerable.Repeat<decimal?>(null, candles.Count));
                return result;
            }
            decimal weightSum = (decimal)period * (period + 1) / 2m;

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < period - 1)
                {
                    result.Add(null);
                    continue;
                }
                decimal sum = 0;
                for (int j = 0; j < period; j++)
                {
                    sum += candles[i - j].Close * (period - j);
                }
                result.Add(weightSum == 0 ? null : (decimal?)(sum / weightSum));
            }
            return result;
        }
    }
}
