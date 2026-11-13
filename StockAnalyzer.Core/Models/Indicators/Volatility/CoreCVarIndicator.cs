using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Buffers;
using System.Collections.Generic;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.CVaR)]
    public class CoreCVarIndicator : CoreIndicatorBase
    {
        public override string Name => $"CVaR({Period}, {ConfidenceLevel:P})";
        public int Period { get; set; } = IndicatorDefaultConstants.VarPeriod;
        public double ConfidenceLevel { get; set; } = IndicatorDefaultConstants.VarConfidenceLevel;

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

            if (Period <= 0)
            {
                // The average of an empty selection used to fail here with this message; the calculation keeps reporting that failure.
                throw new InvalidOperationException("Sequence contains no elements");
            }

            if (Period >= candles.Count)
            {
                // Not one window fits (a window needs Period returns and there are Count - 1): every value is null, without a buffer or the returns.
                for (int i = 0; i < candles.Count; i++) _values.Add(null);
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);
            // Same returns as CoreVarIndicator: an undefined return (a missing price, or a previous price <= 0) is null, never a 0 % day.
            decimal?[] returns = RollingTailMath.ComputeReturns(priceSeries);

            // One buffer for every window: on the stack for a short Period, rented from the pool for a long one.
            decimal[]? rented = null;
            Span<decimal> buffer = Period <= RollingTailMath.MaxStackWindow
                ? stackalloc decimal[Period]
                : (rented = ArrayPool<decimal>.Shared.Rent(Period));
            try
            {
                _values.Add(null); // bar 0 has no return; aligns the series with the candle count

                // Number of the smallest returns that are averaged: the quantile index plus one, at most the whole window.
                int varIndex = (int)Math.Floor((1 - ConfidenceLevel) * Period);
                int tailCount = Math.Min(varIndex + 1, Period);

                for (int i = 0; i < candles.Count - 1; i++)
                {
                    if (i < Period - 1 || !RollingTailMath.TryLoadSortedWindow(returns, i - Period + 1, Period, buffer))
                    {
                        _values.Add(null);
                        continue;
                    }

                    if (tailCount <= 0)
                    {
                        // A confidence level above 1 selects nothing; the average of nothing used to fail with this message.
                        throw new InvalidOperationException("Sequence contains no elements");
                    }

                    decimal sum = 0m;
                    for (int k = 0; k < tailCount; k++)
                    {
                        sum += buffer[k];
                    }

                    _values.Add(sum / tailCount);
                }
            }
            finally
            {
                if (rented != null)
                {
                    ArrayPool<decimal>.Shared.Return(rented);
                }
            }

            return IndicatorResult.Success(_values);
        }
    }
}
