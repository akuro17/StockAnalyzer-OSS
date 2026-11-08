using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Oscillators
{
    [StockAnalyzerIndicator(IndicatorType.MaDeviationRate)]
    public class CoreMaDeviationRateIndicator : CoreIndicatorBase
    {
        public override string Name => $"MA Deviation Rate ({Period}, {MaType})";

        public int Period { get; set; } = 25;
        public MovingAverageType MaType { get; set; } = MovingAverageType.SMA;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreMaDeviationRateParameter devParam)
            {
                Period = devParam.Period;
                MaType = devParam.MaType;
            }
            else if (parameters is CoreSmaParameter smaParam)
            {
                Period = smaParam.Period;
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

            IReadOnlyList<decimal?> baselineMa = ComputeBaselineMovingAverage(series, Period, MaType);

            for (int i = 0; i < series.Count; i++)
            {
                if (i >= baselineMa.Count || !baselineMa[i].HasValue || baselineMa[i]!.Value == 0m || !series[i].HasValue)
                {
                    _values.Add(null);
                }
                else
                {
                    decimal deviation = ((series[i]!.Value - baselineMa[i]!.Value) / baselineMa[i]!.Value) * 100m;
                    _values.Add(deviation);
                }
            }

            return IndicatorResult.Success(_values);
        }

        private static IReadOnlyList<decimal?> ComputeBaselineMovingAverage(IReadOnlyList<decimal?> series, int period, MovingAverageType type)
        {
            if (type == MovingAverageType.SMA)
            {
                return IndicatorCalculationHelper.CalculateSmaWithNulls(series, period);
            }

            var indicator = CreateConfiguredIndicator(type, period);
            if (indicator != null)
            {
                var result = indicator.CalculateSeries(series);
                if (result.IsSuccessful)
                {
                    return indicator.Values;
                }
            }

            return IndicatorCalculationHelper.CalculateSmaWithNulls(series, period);
        }

        private static CoreIndicatorBase? CreateConfiguredIndicator(MovingAverageType type, int period)
        {
            return type switch
            {
                MovingAverageType.SMA => CreateWithParam(IndicatorType.SMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.EMA => CreateWithParam(IndicatorType.EMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.WMA => CreateWithParam(IndicatorType.WMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.HMA => CreateWithParam(IndicatorType.HMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.SMMA => CreateWithParam(IndicatorType.SMMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.TMA => CreateWithParam(IndicatorType.TMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.DEMA => CreateWithParam(IndicatorType.DEMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.TEMA => CreateWithParam(IndicatorType.TEMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.LRMA => CreateWithParam(IndicatorType.LRMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.LSMA => CreateWithParam(IndicatorType.LSMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.ZLEMA => CreateWithParam(IndicatorType.ZLEMA, new CoreSmaParameter { Period = period }),
                MovingAverageType.KAMA => CreateWithParam(IndicatorType.KAMA, new CoreKamaParameter { Period = period }),
                MovingAverageType.ALMA => CreateWithParam(IndicatorType.ALMA, new CoreAlmaParameter { Period = period }),
                _ => CreateWithParam(IndicatorType.SMA, new CoreSmaParameter { Period = period })
            };
        }

        private static CoreIndicatorBase? CreateWithParam(IndicatorType indType, CoreIndicatorParameterBase param)
        {
            return IndicatorFactory.CreateStatic(indType, param) as CoreIndicatorBase;
        }
    }
}
