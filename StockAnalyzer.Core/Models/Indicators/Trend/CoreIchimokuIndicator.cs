using StockAnalyzer.Core.Models.Indicators;
using System;
using System.Collections.Generic;
using System.Linq;
using StockAnalyzer.Core.Models.Parameters;
using Math = System.Math;

namespace StockAnalyzer.Core.Models.Indicators.Trend
{
    [StockAnalyzerIndicator(IndicatorType.Ichimoku)]
    public class CoreIchimokuIndicator : CoreIndicatorBase
    {
        public int TenkanPeriod { get; set; } = 9;
        public int KijunPeriod { get; set; } = 26;
        public int SenkouPeriod { get; set; } = 52;

        /// <summary>
        /// Ichimoku displacement in bars (<see cref="CoreIchimokuParameter.Offset"/>, official value 26): the Chikou Span plots the close of bar t at bar t - Displacement,
        /// the Senkou Spans plot the value calculated at bar t at bar t + Displacement. Must be &gt;= 0 - a negative value would plot the cloud into the past (look-ahead).
        /// </summary>
        public int Displacement { get; set; } = CoreIchimokuParameter.DefaultDisplacement;

        public override string Name => "Ichimoku";

        /// <summary>Output series names of this indicator: the single source for the literals (the renderer, the default series colours, the chart cloud detection and <c>BacktestIndicatorEligibility</c> all refer to these).</summary>
        public const string TenkanSenSeriesName = "TenkanSen";
        public const string KijunSenSeriesName = "KijunSen";
        public const string SenkouSpanASeriesName = "SenkouSpanA";
        public const string SenkouSpanBSeriesName = "SenkouSpanB";
        /// <summary>The lagging span carries a FUTURE close at each bar, so a backtest must refuse it (see <c>BacktestIndicatorEligibility</c>).</summary>
        public const string ChikouSpanSeriesName = "ChikouSpan";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreIchimokuParameter p)
            {
                TenkanPeriod = p.TenkanSample;
                KijunPeriod = p.KijunSample;
                SenkouPeriod = p.SenkouBSample; 
                Displacement = p.Offset;
            }
        }

        public List<decimal?> TenkanSen { get; } = new();
        public List<decimal?> KijunSen { get; } = new();
        public List<decimal?> SenkouSpanA { get; } = new();
        public List<decimal?> SenkouSpanB { get; } = new();
        public List<decimal?> ChikouSpan { get; } = new();


        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            TenkanSen.Clear();
            KijunSen.Clear();
            SenkouSpanA.Clear();
            SenkouSpanB.Clear();
            ChikouSpan.Clear();

            if (Displacement < CoreIchimokuParameter.MinDisplacement)
            {
                throw new ArgumentOutOfRangeException(nameof(Displacement), Displacement, $"Ichimoku Displacement must be >= {CoreIchimokuParameter.MinDisplacement}.");
            }

            int count = candles.Count;
            if (count == 0) return IndicatorResult.Success(_values);

            // 1. Calculate base lines (Tenkan, Kijun) first as they are needed for Senkou A
            var tenkanValues = new decimal?[count];
            var kijunValues = new decimal?[count];

            for (int i = 0; i < count; i++)
            {
                // Tenkan-sen
                if (i < TenkanPeriod - 1) tenkanValues[i] = null;
                else tenkanValues[i] = CalculateMidpoint(candles, i, TenkanPeriod);
                TenkanSen.Add(tenkanValues[i]);

                // Kijun-sen
                if (i < KijunPeriod - 1) kijunValues[i] = null;
                else kijunValues[i] = CalculateMidpoint(candles, i, KijunPeriod);
                KijunSen.Add(kijunValues[i]);
            }

            // 2. Chikou Span (Lagging Span): the close of bar t is plotted Displacement bars back (official definition: 26),
            // so bar i carries Close[i + Displacement]; bars whose source bar does not exist stay null.
            for (int i = 0; i < count; i++)
            {
                int targetIndex = i + Displacement;
                ChikouSpan.Add(targetIndex >= 0 && targetIndex < count ? candles[targetIndex].Close : null);
            }

            // 3. Senkou Spans (Leading Spans)
            // Plotted Displacement periods ahead (Shifted Right; default 26).
            // Current value (derived from Today) is plotted at Today + Displacement.
            
            // We need to generate values for:
            // 1. The existing candle range (0 to count-1) -> shifted to (Displacement to count+Displacement-1)
            // 2. We do NOT calculate "future" values from non-existent candles.
            //    We only project WHAT WE KNOW.
            //    The last known candle is at index `count-1`.
            //    Its Senkou Span value is plotted at `count-1 + Displacement`.
            
            // The renderer iterates up to `VisibleCandleCount`.
            // If we have `count` candles, the data list should ideally have `count + Displacement` entries.
            // But `SenkouSpanA/B` lists align with the Source Candles in basic structure?
            // NO, `IndicatorRenderer` uses `i` as the index.
            // If `IndicatorRenderer` iterates `i` from 0 to `count + Displacement`, it expects `SenkouSpanA[i]` to exist.
            
            // So we must pad the BEGINNING with nulls (Displacement nulls)?
            // AND we must extend the END (Displacement values past count)?
            // "Current value is plotted Displacement periods ahead."
            // So: Value calculated at index `i` is stored at index `i + Displacement`.

            // Let's create a sparse array or list big enough.
            int shift = Displacement; // official definition: 26 (independent of the Kijun period)
            int totalArrSize = count + shift;
            
            var spanA = new decimal?[totalArrSize];
            var spanB = new decimal?[totalArrSize];

            for (int i = 0; i < count; i++)
            {
                // Calculate values for index `i`
                decimal? valA = null;
                decimal? valB = null;

                // Span A: (Tenkan + Kijun) / 2
                decimal? t = tenkanValues[i];
                decimal? k = kijunValues[i];
                if (t.HasValue && k.HasValue)
                    valA = (t.Value + k.Value) / 2m;

                // Span B: Midpoint(52)
                if (i >= SenkouPeriod - 1)
                     valB = CalculateMidpoint(candles, i, SenkouPeriod);

                // Store at shifted position
                if (i + shift < totalArrSize)
                {
                    spanA[i + shift] = valA;
                    spanB[i + shift] = valB;
                }
            }
            
            SenkouSpanA.AddRange(spanA);
            SenkouSpanB.AddRange(spanB);

            _values.AddRange(TenkanSen);
            
            // Create result with all series
            var series = new Dictionary<string, IReadOnlyList<decimal?>>
            {
                { "Main", _values }, // Default for legacy
                { TenkanSenSeriesName, TenkanSen },
                { KijunSenSeriesName, KijunSen },
                { SenkouSpanASeriesName, SenkouSpanA },
                { SenkouSpanBSeriesName, SenkouSpanB },
                { ChikouSpanSeriesName, ChikouSpan }
            };

            return IndicatorResult.Success(series);
        }

        private decimal CalculateMidpoint(IReadOnlyList<CoreCandleData> candles, int endIdx, int period)
        {
            decimal high = decimal.MinValue, low = decimal.MaxValue;
            for (int j = 0; j < period; j++)
            {
                int idx = endIdx - j;
                if (idx < 0) break;
                high = System.Math.Max(high, candles[idx].High);
                low = System.Math.Min(low, candles[idx].Low);
            }
            return (high + low) / 2;
        }
    }
}
