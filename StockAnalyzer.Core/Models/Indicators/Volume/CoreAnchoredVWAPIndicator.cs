using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Volume
{
    /// <summary>
    /// VWAP accumulated from an anchor bar. Unit: price-native. Price = typical price (H + L + C) / 3 (fixed).
    /// Anchor: the bar index AnchorIndex, or - when AnchorDate (yyyy-MM-dd) is set - the first bar whose date is on or after it (none = every value null).
    /// Optional bands (ShowBands, default off): VWAP +/- StdDevMultiplier * sigma, sigma = sqrt(max(0, sum(V*(TP-VWAP)^2) / sum(V))) from the anchor bar to this bar,
    /// computed incrementally as sum(TP^2*V)/sum(V) - VWAP^2. Null before the anchor and while the cumulative volume is 0.
    /// </summary>
    [StockAnalyzerIndicator(IndicatorType.AnchoredVWAP)]
    public class CoreAnchoredVWAPIndicator : CoreIndicatorBase
    {
        public override string Name => "Anchored VWAP";
        public override bool IsOverlay => true;

        public const string UpperBandSeriesName = VwapBandMath.UpperBandSeriesName;
        public const string LowerBandSeriesName = VwapBandMath.LowerBandSeriesName;

        public int AnchorIndex { get; set; } = 0;

        /// <summary>Anchor date as yyyy-MM-dd; empty = use <see cref="AnchorIndex"/>.</summary>
        public string AnchorDate { get; set; } = string.Empty;

        /// <summary>When true the bands are calculated and returned; when false only Main is.</summary>
        public bool ShowBands { get; set; }

        /// <summary>Band width in volume-weighted standard deviations.</summary>
        public decimal StdDevMultiplier { get; set; } = IndicatorDefaultConstants.VwapStdDevMultiplier;

        // Private on purpose: the bands exist only while ShowBands is on, so they must not enter the output catalog (see CoreVwapIndicator).
        private readonly List<decimal?> _upperBand = new();
        private readonly List<decimal?> _lowerBand = new();

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreAnchoredVwapParameter p)
            {
                AnchorIndex = p.AnchorIndex;
                AnchorDate = p.AnchorDate;
                ShowBands = p.ShowBands;
                StdDevMultiplier = p.StdDevMultiplier;
            }
        }

        /// <summary>The class name does not match the naming convention of the parameter lookup (case), so the parameter object is attached explicitly.</summary>
        public override CoreIndicatorSettings GetDefaultSettings()
        {
            CoreIndicatorSettings settings = base.GetDefaultSettings();
            settings.ParameterObject = new CoreAnchoredVwapParameter();
            return settings;
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            _upperBand.Clear();
            _lowerBand.Clear();

            int anchor = ResolveAnchor(candles);
            if (anchor < 0 || anchor >= candles.Count)
            {
                // No valid anchor: fill with nulls.
                for (int i = 0; i < candles.Count; i++)
                {
                    _values.Add(null);
                    if (ShowBands)
                    {
                        _upperBand.Add(null);
                        _lowerBand.Add(null);
                    }
                }

                return BuildResult();
            }

            for (int i = 0; i < anchor; i++)
            {
                _values.Add(null);
                if (ShowBands)
                {
                    _upperBand.Add(null);
                    _lowerBand.Add(null);
                }
            }

            decimal cumulativePriceVolume = 0;
            long cumulativeVolume = 0;
            decimal cumulativePriceSquaredVolume = 0;
            bool bandsOverflowed = false;

            for (int i = anchor; i < candles.Count; i++)
            {
                var candle = candles[i];
                decimal typicalPrice = (candle.High + candle.Low + candle.Close) / 3;
                cumulativePriceVolume += typicalPrice * candle.Volume;
                cumulativeVolume += candle.Volume;
                if (ShowBands && !bandsOverflowed)
                {
                    try
                    {
                        cumulativePriceSquaredVolume += typicalPrice * typicalPrice * candle.Volume;
                    }
                    catch (OverflowException)
                    {
                        // The bands stay null for the rest of the series; the VWAP line is not affected.
                        bandsOverflowed = true;
                    }
                }

                decimal? vwap = cumulativeVolume > 0 ? cumulativePriceVolume / cumulativeVolume : null;
                _values.Add(vwap);

                if (ShowBands)
                {
                    AddBands(vwap, cumulativePriceSquaredVolume, cumulativeVolume, bandsOverflowed);
                }
            }

            return BuildResult();
        }

        // The anchor bar: by date (first bar on or after it, binary search over the chronological bars; -1 when there is none or the text is not a date) or by index.
        private int ResolveAnchor(IReadOnlyList<CoreCandleData> candles)
        {
            if (string.IsNullOrEmpty(AnchorDate))
            {
                return AnchorIndex;
            }

            if (!CoreAnchoredVwapParameter.TryParseAnchorDate(AnchorDate, out DateTime anchorDate))
            {
                return -1;
            }

            int low = 0;
            int high = candles.Count;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (candles[mid].Timestamp.Date >= anchorDate)
                {
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                }
            }

            return low < candles.Count ? low : -1;
        }

        private void AddBands(decimal? vwap, decimal sumPriceSquaredVolume, long sumVolume, bool overflowed)
        {
            bool computed = VwapBandMath.TryComputeBands(vwap, sumPriceSquaredVolume, sumVolume, overflowed, StdDevMultiplier, out decimal upper, out decimal lower);
            _upperBand.Add(computed ? upper : null);
            _lowerBand.Add(computed ? lower : null);
        }

        private IIndicatorResult BuildResult()
        {
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
    }
}
