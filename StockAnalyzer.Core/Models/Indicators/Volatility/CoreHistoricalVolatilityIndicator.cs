using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using Math = System.Math;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.HistoricalVolatility)]
    public class CoreHistoricalVolatilityIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 20;
        public override string Name => $"HV ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period; 
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < Period + 1)
            {
                for(int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period) { _values.Add(null); continue; }

                var returns = new List<double>();
                for (int j = 0; j < Period; j++)
                {
                    int idx = i - j;
                    if (candles[idx - 1].Close != 0)
                        returns.Add(System.Math.Log((double)(candles[idx].Close / candles[idx - 1].Close)));
                }

                if (returns.Count < Period)
                {
                    _values.Add(null);
                    continue;
                }

                double mean = returns.Average();
                double sumOfSquares = returns.Sum(r => (r - mean) * (r - mean));

                // Use sample standard deviation (N-1)
                if (returns.Count < 2)
                {
                     _values.Add(0);
                     continue;
                }
                double variance = sumOfSquares / (returns.Count - 1);
                double stdDev = System.Math.Sqrt(variance);

                // Annualize it (assuming 252 trading days)
                double hv = stdDev * System.Math.Sqrt(252) * 100;

                _values.Add((decimal)hv);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
