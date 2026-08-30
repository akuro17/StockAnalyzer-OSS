using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockAnalyzer.Core.Models.Indicators.Statistics
{
    public class CoreCorrelationIndicator : CoreIndicatorBase
    {
        public override string Name => $"Correlation({Period})";
        public int Period { get; }
        private readonly IEnumerable<decimal> _seriesB;

        public CoreCorrelationIndicator(int period, IEnumerable<CoreCandleData> seriesB)
        {
            Period = period;
            _seriesB = seriesB.Select(c => c.Close);
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            var seriesA = candles.Select(c => c.Close).ToList();
            var seriesBList = _seriesB.ToList();

            int dataSize = Math.Min(seriesA.Count, seriesBList.Count);

            for (int i = 0; i < dataSize; i++)
            {
                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                var rangeA = seriesA.Skip(i - Period + 1).Take(Period).ToList();
                var rangeB = seriesBList.Skip(i - Period + 1).Take(Period).ToList();

                var meanA = rangeA.Average();
                var meanB = rangeB.Average();

                decimal sumA = rangeA.Sum(d => (d - meanA) * (d - meanA));
                decimal sumB = rangeB.Sum(d => (d - meanB) * (d - meanB));
                decimal sumAB = rangeA.Zip(rangeB, (a, b) => (a - meanA) * (b - meanB)).Sum();

                if (sumA == 0 || sumB == 0)
                {
                    _values.Add(0); // Or null, depending on convention for zero variance
                }
                else
                {
                    var correlation = sumAB / (decimal)System.Math.Sqrt((double)sumA * (double)sumB);
                    _values.Add(correlation);
                }
            }

            // Pad the end with nulls if seriesA is longer than seriesB
            for(int i = dataSize; i < seriesA.Count; i++)
            {
                _values.Add(null);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
