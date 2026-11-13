using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;
using System;
using System.Collections.Generic;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    /// <summary>
    /// Volume weighted average price. Unit: price-native.
    /// VWAP_i = sum(P*V) / sum(V) over the bars since the last reset (ResetPeriod; Cumulative = since the first bar).
    /// A bar whose price is null adds to neither the numerator nor the denominator (all modes). Without volume in the sums the value is null.
    /// Optional bands (ShowBands, default off): VWAP +/- StdDevMultiplier * sigma, sigma = sqrt(max(0, sum(V*(P-VWAP)^2) / sum(V))) within the same period,
    /// computed incrementally as sum(P^2*V)/sum(V) - VWAP^2.
    /// </summary>
    [StockAnalyzerIndicator(IndicatorType.VWAP)]
    public class CoreVwapIndicator : CoreIndicatorBase
    {
        public override string Name => "VWAP";
        public override bool IsOverlay => true;

        public const string UpperBandSeriesName = VwapBandMath.UpperBandSeriesName;
        public const string LowerBandSeriesName = VwapBandMath.LowerBandSeriesName;

        // Quarter key = year * 10 + quarter (1..4); a quarter spans three months.
        private const int QuarterKeyYearMultiplier = 10;
        private const int MonthsPerQuarter = 3;

        /// <summary>
        /// Price source used to compute the VWAP numerator.
        /// Defaults to Typical Price ((High + Low + Close) / 3), the industry-standard VWAP source.
        /// </summary>
        public override PriceType PriceSource { get; set; } = PriceType.Typical;

        /// <summary>When the cumulative sums restart. Default Cumulative (never), the behaviour before this parameter existed.</summary>
        public VwapResetPeriod ResetPeriod { get; set; } = VwapResetPeriod.Cumulative;

        /// <summary>When true the bands are calculated and returned; when false only Main is.</summary>
        public bool ShowBands { get; set; }

        /// <summary>Band width in volume-weighted standard deviations.</summary>
        public decimal StdDevMultiplier { get; set; } = IndicatorDefaultConstants.VwapStdDevMultiplier;

        // The band series are deliberately NOT public properties: output-name discovery (screener, backtest, registration dialogs) lists public series
        // properties, and the bands exist only while ShowBands is on (default off), so listing them would offer outputs a default VWAP does not emit.
        // They are chart / Data Window series that appear in the result when ShowBands is on (same idea as the Camarilla-only R4/S4 of PivotPointsAdvanced).
        private readonly List<decimal?> _upperBand = new();
        private readonly List<decimal?> _lowerBand = new();

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreVwapParameter p)
            {
                ResetPeriod = p.ResetPeriod;
                ShowBands = p.ShowBands;
                StdDevMultiplier = p.StdDevMultiplier;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
            => CalculateWithPrices(candles, PriceDataHelper.ExtractPriceSeries(candles, PriceSource));

        /// <summary>
        /// The calculation for an explicit price series (one entry per candle; null = no price for that bar). CalculateCore passes the series of the
        /// PriceSource; the seam exists so the null-price rule can be tested with a series that a candle list alone cannot produce.
        /// </summary>
        internal IIndicatorResult CalculateWithPrices(IReadOnlyList<CoreCandleData> candles, IReadOnlyList<decimal?> priceSeries)
        {
            _values.Clear();
            _upperBand.Clear();
            _lowerBand.Clear();

            decimal sumPriceVolume = 0m;
            decimal sumVolume = 0m;
            decimal sumPriceSquaredVolume = 0m;
            bool bandsOverflowed = false;
            int previousKey = 0;

            for (int i = 0; i < candles.Count; i++)
            {
                if (ResetPeriod != VwapResetPeriod.Cumulative)
                {
                    int key = PeriodKey(candles[i].Timestamp, ResetPeriod);
                    if (i > 0 && key != previousKey)
                    {
                        sumPriceVolume = 0m;
                        sumVolume = 0m;
                        sumPriceSquaredVolume = 0m;
                        bandsOverflowed = false;
                    }

                    previousKey = key;
                }

                // A bar without a price is left out of numerator and denominator.
                if (priceSeries[i] is { } price)
                {
                    decimal volume = candles[i].Volume;
                    sumPriceVolume += price * volume;
                    sumVolume += volume;
                    if (ShowBands && !bandsOverflowed)
                    {
                        try
                        {
                            sumPriceSquaredVolume += price * price * volume;
                        }
                        catch (OverflowException)
                        {
                            // The bands stay null until the next reset; the VWAP line is not affected.
                            bandsOverflowed = true;
                        }
                    }
                }

                decimal? vwap = sumVolume == 0m ? null : sumPriceVolume / sumVolume;
                _values.Add(vwap);

                if (ShowBands)
                {
                    AddBands(vwap, sumPriceSquaredVolume, sumVolume, bandsOverflowed);
                }
            }

            if (!ShowBands)
            {
                return IndicatorResult.Success(_values);
            }

            return IndicatorResult.Success(new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { IndicatorResult.MainSeriesName, _values },
                { UpperBandSeriesName, _upperBand },
                { LowerBandSeriesName, _lowerBand },
            });
        }

        private void AddBands(decimal? vwap, decimal sumPriceSquaredVolume, decimal sumVolume, bool overflowed)
        {
            bool computed = VwapBandMath.TryComputeBands(vwap, sumPriceSquaredVolume, sumVolume, overflowed, StdDevMultiplier, out decimal upper, out decimal lower);
            _upperBand.Add(computed ? upper : null);
            _lowerBand.Add(computed ? lower : null);
        }

        private static int PeriodKey(DateTime timestamp, VwapResetPeriod period) => period switch
        {
            VwapResetPeriod.Weekly => TimeFrameAggregator.GetPeriodKey(timestamp, TimeFrame.W1),
            VwapResetPeriod.Monthly => TimeFrameAggregator.GetPeriodKey(timestamp, TimeFrame.MN1),
            VwapResetPeriod.Quarterly => timestamp.Year * QuarterKeyYearMultiplier + ((timestamp.Month - 1) / MonthsPerQuarter + 1),
            VwapResetPeriod.Yearly => timestamp.Year,
            _ => 0,
        };
    }
}
