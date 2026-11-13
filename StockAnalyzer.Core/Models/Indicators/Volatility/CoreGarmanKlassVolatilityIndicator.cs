using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volatility
{
    /// <summary>
    /// Garman-Klass volatility. Unit: ratio (not percent).
    /// Per-bar variance v_k = 0.5 * ln(H/L)^2 - (2 ln 2 - 1) * ln(C/O)^2.
    /// Default (Period 1, Annualize off): sqrt(v_k) of each bar alone, null when v_k is negative or any price is not positive (the original definition, unchanged).
    /// Period above 1: sqrt(max(0, mean of v_k over the last Period bars)) - the square root of the mean VARIANCE, not the mean of the per-bar standard deviations;
    /// null while fewer than Period bars exist or when a bar of the window has a non-positive price or a non-finite v_k (a negative v_k is a valid input there).
    /// Annualize on: the value is multiplied by sqrt(PeriodsPerYear); off, it does not depend on PeriodsPerYear.
    /// </summary>
    [StockAnalyzerIndicator(IndicatorType.GarmanKlassVolatility)]
    public class CoreGarmanKlassVolatilityIndicator : CoreIndicatorBase
    {
        public override string Name => "Garman-Klass Volatility";

        /// <summary>Bars aggregated (1 = each bar alone).</summary>
        public int Period { get; set; } = IndicatorDefaultConstants.GarmanKlassPeriod;

        /// <summary>When true the value is multiplied by sqrt(<see cref="PeriodsPerYear"/>).</summary>
        public bool Annualize { get; set; }

        /// <summary>Bars per year for the annualisation; used only when <see cref="Annualize"/> is true.</summary>
        public int PeriodsPerYear { get; set; } = IndicatorDefaultConstants.PeriodsPerYear;

        // 2 ln 2 - 1, the weight of the close-open term (the one definition for both paths).
        private static readonly double CloseOpenWeight = 2.0 * Math.Log(2.0) - 1.0;

        // v = 0.5 * ln(H/L)^2 - (2 ln 2 - 1) * ln(C/O)^2 for a bar whose four prices are positive (the caller checks); may be negative or non-finite.
        private static double BarVariance(CoreCandleData candle)
        {
            var h_l = Math.Log((double)(candle.High / candle.Low));
            var c_o = Math.Log((double)(candle.Close / candle.Open));
            return 0.5 * h_l * h_l - CloseOpenWeight * c_o * c_o;
        }

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreGarmanKlassVolatilityParameter p)
            {
                Period = p.Period;
                Annualize = p.Annualize;
                PeriodsPerYear = p.PeriodsPerYear;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            if (Period == IndicatorDefaultConstants.GarmanKlassPeriod && !Annualize)
            {
                return CalculatePerBar(candles);
            }

            return CalculateAggregated(candles);
        }

        // The original calculation, untouched.
        private IIndicatorResult CalculatePerBar(IReadOnlyList<CoreCandleData> candles)
        {

            foreach (var candle in candles)
            {
                if (candle.Open <= 0 || candle.High <= 0 || candle.Low <= 0 || candle.Close <= 0)
                {
                    _values.Add(null);
                    continue;
                }

                var variance = BarVariance(candle);

                if (!double.IsFinite(variance) || variance < 0)
                {
                    _values.Add(null);
                }
                else
                {
                    var volatility = (decimal)Math.Sqrt(variance);
                    _values.Add(volatility);
                }
            }

            return IndicatorResult.Success(_values);
        }

        private IIndicatorResult CalculateAggregated(IReadOnlyList<CoreCandleData> candles)
        {
            int count = candles.Count;
            double annualFactor = Annualize ? Math.Sqrt(PeriodsPerYear) : 1.0;

            // variances[k] is the per-bar variance; valid[k] is false for a non-positive price or a non-finite variance.
            var variances = new double[count];
            var valid = new bool[count];
            for (int k = 0; k < count; k++)
            {
                CoreCandleData candle = candles[k];
                if (candle.Open <= 0 || candle.High <= 0 || candle.Low <= 0 || candle.Close <= 0)
                {
                    continue;
                }

                double variance = BarVariance(candle);
                if (double.IsFinite(variance))
                {
                    variances[k] = variance;
                    valid[k] = true;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (i < Period - 1)
                {
                    _values.Add(null);
                    continue;
                }

                double sum = 0.0;
                bool windowValid = true;
                for (int k = i - Period + 1; k <= i; k++)
                {
                    if (!valid[k])
                    {
                        windowValid = false;
                        break;
                    }

                    sum += variances[k];
                }

                if (!windowValid)
                {
                    _values.Add(null);
                    continue;
                }

                _values.Add(IndicatorCalculationHelper.ToDecimalOrNull(Math.Sqrt(Math.Max(0.0, sum / Period)) * annualFactor));
            }

            return IndicatorResult.Success(_values);
        }
    }
}
