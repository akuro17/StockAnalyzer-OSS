using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.MaDeviationRate)]
    public class CoreMaDeviationRateIndicator : CoreIndicatorBase
    {
        public override string Name => "MA Deviation Rate";

        public int Period { get; set; } = 25;

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

            if (series.Count < Period || Period <= 0)
            {
                for (int i = 0; i < series.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var sma = IndicatorCalculationHelper.CalculateSmaWithNulls(series, Period);

            for (int i = 0; i < series.Count; i++)
            {
                if (!sma[i].HasValue || sma[i]!.Value == 0m || !series[i].HasValue)
                {
                    _values.Add(null);
                }
                else
                {
                    decimal deviation = ((series[i]!.Value - sma[i]!.Value) / sma[i]!.Value) * 100m;
                    _values.Add(deviation);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
