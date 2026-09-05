using StockAnalyzer.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.ElderForceIndex)]
    public class CoreElderForceIndexIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 13;
        public override string Name => $"EFI ({Period})";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreSmaParameter p)
            {
                Period = p.Period;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (candles.Count < 2) return IndicatorResult.Success(_values);
            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            var forceIndex = new List<decimal> { 0 };
            for (int i = 1; i < candles.Count; i++)
                forceIndex.Add(((priceSeries[i] ?? 0m) - (priceSeries[i - 1] ?? 0m)) * candles[i].Volume);

            // Apply EMA
            decimal mult = 2m / (Period + 1);
            decimal? ema = null;
            _values.Add(null);
            for (int i = 1; i < forceIndex.Count; i++)
            {
                if (i < Period) { _values.Add(null); continue; }
                if (ema == null) { ema = forceIndex.Skip(1).Take(Period).Average(); } // Skip first 0 value
                else { ema = (forceIndex[i] - ema.Value) * mult + ema.Value; }
                _values.Add(ema);
            }

            return IndicatorResult.Success(_values);
        }
    }
}
