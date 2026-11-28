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
            _values.Clear();
            if (candles.Count < 2) return IndicatorResult.Success(_values);

            if (Period <= 0)
            {
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            var forceIndex = new List<decimal?>(candles.Count) { null };
            for (int i = 1; i < candles.Count; i++)
            {
                decimal? curr = priceSeries[i];
                decimal? prev = priceSeries[i - 1];
                forceIndex.Add(curr.HasValue && prev.HasValue
                    ? (curr.Value - prev.Value) * candles[i].Volume
                    : null);
            }

            _values.AddRange(IndicatorCalculationHelper.CalculateEmaWithNulls(forceIndex, Period));
            return IndicatorResult.Success(_values);
        }
    }
}
