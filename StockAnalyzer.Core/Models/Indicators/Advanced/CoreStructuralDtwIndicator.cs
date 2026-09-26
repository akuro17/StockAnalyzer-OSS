using System;
using System.Collections.Generic;
using System.Buffers;
using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models.Parameters;

namespace StockAnalyzer.Core.Models.Indicators.Advanced
{
    /// <summary>
    /// DTW distance between the Z-normalized current window [i - Period + 1 .. i] and the lagged window
    /// [i - Period - Lag + 1 .. i - Lag] at each bar i (no future bars). The first value is at index Period + Lag - 1.
    /// When Lag &lt; Period the two windows overlap and share prices, which lowers the distance.
    /// Both windows flat yields 0 (a shape-only comparison, price level is ignored); exactly one flat yields null;
    /// a missing price in either window yields null.
    /// Output unit: dimensionless DTW distance between Z-normalized windows (sqrt of the accumulated squared
    /// Z-score differences); 0 means identical shape. The computation runs in double on Z-normalized values and
    /// the result is converted to decimal at the output boundary.
    /// </summary>
    [StockAnalyzerIndicator(IndicatorType.StructuralDtw)]
    public class CoreStructuralDtwIndicator : CoreIndicatorBase
    {
        public override string Name => "Structural DTW Oscillator";
        public string ShortName => $"DTW_Osc({Period},{Lag})";

        public int Period { get; set; } = 14;
        public int Lag { get; set; } = 14;
        public int WarpingRadius { get; set; } = IndicatorDefaultConstants.DtwDefaultWarpingRadius;

        public override void Configure(CoreIndicatorParameterBase parameters)
        {
            if (parameters is CoreStructuralDtwParameter p)
            {
                Period = p.Period;
                Lag = p.Lag;
                WarpingRadius = p.WarpingRadius;
            }
        }

        protected override IIndicatorResult CalculateCore(IReadOnlyList<CoreCandleData> candles)
        {
            int count = candles.Count;
            _values.Clear();
            _values.Capacity = count;

            if (count < Period + Lag)
            {
                for (int i = 0; i < count; i++)
                {
                    _values.Add(null);
                }
                return IndicatorResult.Success(_values);
            }

            var priceSeries = PriceDataHelper.ExtractPriceSeries(candles, PriceSource);

            double[]? w1HeapBuffer = null;
            Span<double> w1Norm = Period <= MathBufferLimits.StackAllocThreshold
                ? stackalloc double[Period]
                : (w1HeapBuffer = ArrayPool<double>.Shared.Rent(Period)).AsSpan(0, Period);

            double[]? w2HeapBuffer = null;
            Span<double> w2Norm = Period <= MathBufferLimits.StackAllocThreshold
                ? stackalloc double[Period]
                : (w2HeapBuffer = ArrayPool<double>.Shared.Rent(Period)).AsSpan(0, Period);

            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (i < Period + Lag - 1)
                    {
                        _values.Add(null);
                        continue;
                    }

                    // Current window [i - Period + 1 .. i] and lagged window [i - Period - Lag + 1 .. i - Lag]; no future bars.
                    if (!PriceDataHelper.TryLoadWindow(priceSeries, i - Period + 1, w1Norm) || !PriceDataHelper.TryLoadWindow(priceSeries, i - Period - Lag + 1, w2Norm))
                    {
                        // A missing price makes the comparison undefined; substituting a value would distort the normalization.
                        _values.Add(null);
                        continue;
                    }

                    bool w1Flat = NormalizeInPlace(w1Norm);
                    bool w2Flat = NormalizeInPlace(w2Norm);

                    if (w1Flat && w2Flat)
                    {
                        _values.Add(0.0m);
                    }
                    else if (w1Flat || w2Flat)
                    {
                        _values.Add(null);
                    }
                    else
                    {
                        double dist = DtwMath.Calculate(w1Norm, w2Norm, WarpingRadius);
                        _values.Add(double.IsNaN(dist) ? null : (decimal)dist);
                    }
                }

                return IndicatorResult.Success(_values);
            }
            finally
            {
                if (w1HeapBuffer != null) ArrayPool<double>.Shared.Return(w1HeapBuffer);
                if (w2HeapBuffer != null) ArrayPool<double>.Shared.Return(w2HeapBuffer);
            }
        }

        /// <summary>
        /// Z-normalizes <paramref name="window"/> in place. Returns true (window left un-normalized) when the window is flat.
        /// </summary>
        private static bool NormalizeInPlace(Span<double> window) => !ZNormalization.TryNormalize(window, window);
    }
}
