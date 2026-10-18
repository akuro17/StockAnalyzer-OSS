using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.DPO)]
    public class CoreDpoIndicator : CoreIndicatorBase
    {
        public int Period { get; set; } = 20;
        public override string Name => $"DPO ({Period})";

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

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            return CalculateSeriesCore(priceSeries, null);
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            _values.Clear();
            if (series == null || series.Count == 0) return IndicatorResult.Success(_values);

            if (dynamicPeriods != null && dynamicPeriods.Count > 0)
            {
                var adaptiveSma = AdaptiveSmoothingHelper.CalculateAdaptiveSma(series, dynamicPeriods, Period);
                for (int i = 0; i < series.Count; i++)
                {
                    int p = (int)Math.Round(dynamicPeriods[i] ?? Period, MidpointRounding.AwayFromZero);
                    if (p <= 0) p = Period;
                    int shift = (p / 2) + 1;
                    int k = i - shift;

                    if (k >= 0 && k < adaptiveSma.Count && series[i].HasValue && adaptiveSma[k].HasValue)
                    {
                        _values.Add(series[i]!.Value - adaptiveSma[k]!.Value);
                    }
                    else
                    {
                        _values.Add(null);
                    }
                }

                return IndicatorResult.Success(_values);
            }

            int staticShift = (Period / 2) + 1;
            var sma = IndicatorCalculationHelper.CalculateSmaWithNulls(series, Period);

            for (int i = 0; i < series.Count; i++)
            {
                int k = i - staticShift;
                if (k >= Period - 1 && k < sma.Count && series[i].HasValue && sma[k].HasValue)
                {
                    _values.Add(series[i]!.Value - sma[k]!.Value);
                }
                else
                {
                    _values.Add(null);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
