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

        public override string Name => "Ichimoku";

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreIchimokuParameter p)
            {
                TenkanPeriod = p.TenkanSample;
                KijunPeriod = p.KijunSample;
                SenkouPeriod = p.SenkouBSample; 
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

            // 2. Chikou Span (Lagging Span)
            // Plotted 26 periods behind (Left shift).
            // Current Close is plotted at i - 25 (if 1-based) or i - 26?
            // "Chikou Span is the Closing Price plotted 26 periods in the past."
            // So logic: ChikouSpan[i] = Close of (i + 26)
            for (int i = 0; i < count; i++)
            {
                if (i + KijunPeriod - 1 < count) // shifted index exists
                {
                    // In many implementations: Chikou[i] corresponds to Candle[i].
                    // But here we return lists that align with Candle[0]..Candle[N].
                    // If we want to show Close[N] at index N-26:
                    // ChikouSpan[i] should hold the value that should be drawn at i.
                    // This means ChikouSpan[i] = Close[i + 26 - 1] usually (offset is often period-1).
                    // Standard Ichimoku uses 26 displacement.
                    // Let's assume KijunPeriod=26.
                    // Value at T is Close(T+26).
                    
                    // Wait, if it's lagging, the Close of TODAY is drawn 26 days AGO.
                    // So at index (Today - 26), we should see Today's Close.
                    // ChikouSpan[i] = Close at (i + 26)? 
                    // No, Close[i] is drawn at [i-26].
                    // So array alignment:
                    // ChikouSpan[i] = Close[i + 26 - 1]. (Commonly offset is 26 including current day)
                    // If i + 25 < count, ChikouSpan[i] = candles[i+25].Close.
                    // If we use strictly Period (26):
                    int targetIndex = i + KijunPeriod - 1; 
                    if (targetIndex < count)
                         ChikouSpan.Add(candles[targetIndex].Close);
                    else
                         ChikouSpan.Add(null);
                }
                else
                {
                    ChikouSpan.Add(null);
                }
            }

            // 3. Senkou Spans (Leading Spans)
            // Plotted 26 periods ahead (Shifted Right).
            // Current value (derived from Today) is plotted at Today + 26.
            
            // We need to generate values for:
            // 1. The existing candle range (0 to count-1) -> shifted to (26 to count+25)
            // 2. We do NOT calculate "future" values from non-existent candles.
            //    We only project WHAT WE KNOW.
            //    The last known candle is at index `count-1`.
            //    Its Senkou Span value is plotted at `count-1 + 26`.
            
            // The renderer iterates up to `VisibleCandleCount`.
            // If we have `count` candles, the data list should ideally have `count + 26` entries.
            // But `SenkouSpanA/B` lists align with the Source Candles in basic structure?
            // NO, `IndicatorRenderer` uses `i` as the index.
            // If `IndicatorRenderer` iterates `i` from 0 to `count + 26`, it expects `SenkouSpanA[i]` to exist.
            
            // So we must pad the BEGINNING with nulls (26 nulls)?
            // AND we must extend the END (26 values past count)?
            // "Current value is plotted 26 periods ahead."
            // So: Value calculated at index `i` is stored at index `i + 26`.

            // Let's create a sparse array or list big enough.
            int shift = KijunPeriod; // 26
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
                { "TenkanSen", TenkanSen },
                { "KijunSen", KijunSen },
                { "SenkouSpanA", SenkouSpanA },
                { "SenkouSpanB", SenkouSpanB },
                { "ChikouSpan", ChikouSpan }
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
