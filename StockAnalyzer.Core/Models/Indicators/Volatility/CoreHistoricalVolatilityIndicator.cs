using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Buffers;
using System.Collections.Generic;
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
            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            return CalculateSeriesCore(priceSeries);
        }

        protected override IIndicatorResult CalculateSeriesCore(IReadOnlyList<decimal?> series, IReadOnlyList<decimal?>? dynamicPeriods = null)
        {
            if (Period < 2 || series.Count < Period + 1)
            {
                for (int i = 0; i < series.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            double[]? rented = null;
            try
            {
                Span<double> returns = Period <= MathBufferLimits.StackAllocThreshold
                    ? stackalloc double[Period]
                    : (rented = ArrayPool<double>.Shared.Rent(Period)).AsSpan(0, Period);

                double sqrtPeriodsPerYear = Math.Sqrt(IndicatorDefaultConstants.PeriodsPerYear);

                for (int i = 0; i < series.Count; i++)
                {
                    if (i < Period) { _values.Add(null); continue; }

                    bool windowValid = true;
                    double sum = 0.0;
                    for (int j = 0; j < Period; j++)
                    {
                        int idx = i - j;
                        decimal? currOpt = series[idx];
                        decimal? prevOpt = series[idx - 1];
                        if (!currOpt.HasValue || !prevOpt.HasValue)
                        {
                            windowValid = false;
                            break;
                        }

                        decimal curr = currOpt.Value;
                        decimal prev = prevOpt.Value;
                        if (curr <= 0m || prev <= 0m)
                        {
                            windowValid = false;
                            break;
                        }

                        double r = Math.Log((double)(curr / prev));
                        if (!double.IsFinite(r))
                        {
                            windowValid = false;
                            break;
                        }

                        returns[j] = r;
                        sum += r;
                    }

                    if (!windowValid)
                    {
                        _values.Add(null);
                        continue;
                    }

                    double mean = sum / Period;
                    double sumOfSquares = 0.0;
                    for (int j = 0; j < Period; j++)
                    {
                        double diff = returns[j] - mean;
                        sumOfSquares += diff * diff;
                    }

                    // Use sample standard deviation (N-1)
                    double variance = sumOfSquares / (Period - 1);
                    double stdDev = Math.Sqrt(variance);

                    // Annualize it (assuming 252 trading days)
                    double hv = stdDev * sqrtPeriodsPerYear * IndicatorDefaultConstants.PercentScale;

                    _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(hv));
                }
            }
            finally
            {
                if (rented != null)
                {
                    ArrayPool<double>.Shared.Return(rented);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
