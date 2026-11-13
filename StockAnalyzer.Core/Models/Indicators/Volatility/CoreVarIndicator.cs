using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    [StockAnalyzerIndicator(IndicatorType.VaR)]
    public class CoreVarIndicator : CoreIndicatorBase
    {
        public override string Name => $"VaR({Period}, {ConfidenceLevel:P})";
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

        // Same parameter type and initial values as the DefaultCoreIndicatorSettings entry (the naming rule would find none).
        public override CoreIndicatorSettings GetDefaultSettings()
        {
            var settings = base.GetDefaultSettings();
            settings.ParameterObject = new CoreCVarParameter
            {
                Period = IndicatorDefaultConstants.VarPeriod,
                ConfidenceLevel = IndicatorDefaultConstants.VarConfidenceLevel
            };
            return settings;
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
            decimal?[] returns = RollingTailMath.ComputeReturns(priceSeries);

            // One buffer for every window: on the stack for a short Period, rented from the pool for a long one.
            decimal[]? rented = null;
            Span<decimal> buffer = Period <= RollingTailMath.MaxStackWindow
                ? stackalloc decimal[Period]
                : (rented = ArrayPool<decimal>.Shared.Rent(Period));
            try
            {
                // The quantile index depends on Period and the confidence level only.
                int index = (int)Math.Floor((1.0 - ConfidenceLevel) * Period);
                index = Math.Clamp(index, 0, Period - 1);

                for (int i = 0; i < candles.Count; i++)
                {
                    if (i < Period)
                    {
                        _values.Add(null);
                        continue;
                    }

                    _values.Add(RollingTailMath.TryLoadSortedWindow(returns, i - Period, Period, buffer) ? buffer[index] : null);
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
