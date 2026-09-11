using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.UlcerIndex)]
    public class CoreUlcerIndexIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 14;
        public override string Name => $"Ulcer ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period; 
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            for (int i = 0; i < candles.Count; i++)
            {
                if (i < Period - 1) { _values.Add(null); continue; }
                decimal maxClose = 0;
                for (int j = 0; j < Period; j++) maxClose = Math.Max(maxClose, priceSeries[i - j] ?? 0m);

                if (maxClose == 0)
                {
                    _values.Add(0); // Avoid division by zero, drawdown is 0%
                    continue;
                }

                decimal sumSq = 0;
                for (int j = 0; j < Period; j++)
                {
                    decimal dd = ((priceSeries[i - j] ?? 0m) - maxClose) / maxClose * 100;
                    sumSq += dd * dd;
                }
                _values.Add((decimal)Math.Sqrt((double)(sumSq / Period)));
            }

            return IndicatorResult.Success(_values);
        }
    }
}
