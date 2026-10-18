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
            _values.Clear();

            if (candles == null || candles.Count == 0) return IndicatorResult.Success(_values);

            if (candles.Count < Period + 1 || Period <= 0)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            var returns = new decimal?[candles.Count - 1];

            for (int i = 1; i < candles.Count; i++)
            {
                var prev = priceSeries[i - 1];
                var curr = priceSeries[i];
                if (!prev.HasValue || !curr.HasValue || prev.Value <= 0m)
                {
                    returns[i - 1] = null;
                }
                else
                {
                    returns[i - 1] = (curr.Value - prev.Value) / prev.Value;
                }
            }

            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period)
                {
                    _values.Add(null);
                    continue;
                }

                bool hasNull = false;
                var window = new List<decimal>(Period);
                for (int k = i - Period; k < i; k++)
                {
                    var r = returns[k];
                    if (!r.HasValue)
                    {
                        hasNull = true;
                        break;
                    }
                    window.Add(r.Value);
                }

                if (hasNull)
                {
                    _values.Add(null);
                }
                else
                {
                    window.Sort();
                    int index = (int)Math.Floor((1.0 - ConfidenceLevel) * Period);
                    index = Math.Clamp(index, 0, Period - 1);
                    _values.Add(window[index]);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
