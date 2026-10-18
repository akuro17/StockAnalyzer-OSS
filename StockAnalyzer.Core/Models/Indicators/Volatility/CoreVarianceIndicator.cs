using StockAnalyzer.Core.Models.Indicators;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.Variance)]
    public class CoreVarianceIndicator : CoreIndicatorBase
    {
        public override string Name => $"Variance({Period})";
        public override bool IsOverlay => false;
        public int Period { get; set; } = 20;

        /// <summary>
        /// When true and Period > 1, computes sample variance (dividing by Period - 1).
        /// Otherwise computes population variance (dividing by Period).
        /// Default is true to maintain exact compatibility with existing unit tests.
        /// </summary>
        public bool IsSampleVariance { get; set; } = true;

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

            if (candles.Count < Period || Period <= 0)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                bool hasNull = false;
                decimal sum = 0m;
                int start = i - Period + 1;
                for (int j = 0; j < Period; j++)
                {
                    var val = priceSeries[start + j];
                    if (!val.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    sum += val.Value;
                }

                if (hasNull)
                {
                    _values.Add(null);
                    continue;
                }

                decimal mean = sum / Period;
                decimal sumSqDiff = 0m;
                for (int j = 0; j < Period; j++)
                {
                    decimal diff = priceSeries[start + j]!.Value - mean;
                    sumSqDiff += diff * diff;
                }

                decimal divisor = (IsSampleVariance && Period > 1) ? (Period - 1) : Period;
                decimal variance = divisor <= 0m ? 0m : sumSqDiff / divisor;
                _values.Add(variance);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
