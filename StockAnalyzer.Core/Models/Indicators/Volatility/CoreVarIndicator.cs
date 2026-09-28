using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.VaR)]
    public class CoreVarIndicator : CoreIndicatorBase
    {
        public override string Name => $"VaR({Period}, {ConfidenceLevel:P})";
        public int Period { get; set; } = 20;
        public double ConfidenceLevel { get; set; } = 0.95;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreCVarParameter p)
            {
                Period = p.Period;
                ConfidenceLevel = p.ConfidenceLevel;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {

            var returns = new List<decimal>();

            if (candles.Count < 2)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            for (int i = 1; i < candles.Count; i++)
            {
                decimal prev = priceSeries[i - 1] ?? 0m;
                if (prev != 0)
                {
                    returns.Add(((priceSeries[i] ?? 0m) - prev) / prev);
                }
                else
                {
                    returns.Add(0);
                }
            }

            for (int i = 0; i < candles.Count - 1; i++)
            {
                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                var relevantReturns = returns.Skip(i - Period + 1).Take(Period).OrderBy(r => r).ToList();
                int index = (int)System.Math.Floor((1 - ConfidenceLevel) * relevantReturns.Count);
                _values.Add(relevantReturns[index]);
            }
            _values.Insert(0, null); // Align with candle count

            return IndicatorResult.Success(_values);
        }
    }
}
