using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.CVaR)]
    public class CoreCVarIndicator : CoreIndicatorBase
    {
        public override string Name => $"CVaR({Period}, {ConfidenceLevel:P})";
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

            if (candles.Count < 2)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var returns = new List<decimal>();
            for (int i = 1; i < candles.Count; i++)
            {
                returns.Add(candles[i-1].Close != 0 ? (candles[i].Close - candles[i-1].Close) / candles[i-1].Close : 0);
            }

            for (int i = 0; i < candles.Count - 1; i++)
            {
                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                var relevantReturns = returns.Skip(i - Period + 1).Take(Period).OrderBy(r => r).ToList();
                int varIndex = (int)System.Math.Floor((1 - ConfidenceLevel) * relevantReturns.Count);

                decimal cvar = relevantReturns.Take(varIndex + 1).Average();
                _values.Add(cvar);
            }
             _values.Insert(0, null); // Align with candle count

            return IndicatorResult.Success(_values);
        }
    }
}
