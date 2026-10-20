using System;
using System.Buffers;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;
using StockAnalyzer.ZeroAllocation;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// Converts OHLCV candle data into Point &amp; Figure (P&amp;F) columns.
/// Thin adapter wrapping the SSoT engine <see cref="PointAndFigureCalculator"/>.
/// Each returned CandleData represents one P&amp;F column:
///   - Up column (X): Open = column bottom, Close = column top  (IsBullish = true)
///   - Down column (O): Open = column top, Close = column bottom (IsBullish = false)
///   - High = max(Open, Close), Low = min(Open, Close)
/// Standard P&amp;F rules: no overlap between adjacent columns (1-box offset).
/// </summary>
public static class PointAndFigureConverter
{
    /// <summary>
    /// Convert raw candle data into P&amp;F columns.
    /// </summary>
    /// <param name="candles">Source OHLCV candle data.</param>
    /// <param name="boxSize">Box size (price unit per box).</param>
    /// <param name="reversalAmount">Number of boxes required for reversal (typically 3).</param>
    /// <param name="roundingMode">Optional price quantization rounding mode.</param>
    /// <returns>List of CoreCandleData, each representing one P&amp;F column.</returns>
    public static List<CoreCandleData> Convert(
        IReadOnlyList<CoreCandleData> candles,
        decimal boxSize,
        int reversalAmount = ChartConstants.DefaultReversalAmount,
        ChartRoundingMode roundingMode = ChartRoundingMode.None)
    {
        var columns = new List<CoreCandleData>();
        Convert(candles, boxSize, reversalAmount, columns, roundingMode);
        return columns;
    }

    /// <summary>
    /// Convert raw candle data into P&amp;F columns in-place using output buffers.
    /// Delegates directly to <see cref="PointAndFigureCalculator"/> for unified SSoT calculation.
    /// </summary>
    public static void Convert(
        IReadOnlyList<CoreCandleData> candles,
        decimal boxSize,
        int reversalAmount,
        List<CoreCandleData> outputBuffer,
        ChartRoundingMode roundingMode = ChartRoundingMode.None,
        List<ElementDates>? elementDatesBuffer = null)
    {
        outputBuffer.Clear();
        elementDatesBuffer?.Clear();
        if (candles == null || candles.Count == 0) return;
        if (boxSize <= 0) throw new ArgumentOutOfRangeException(nameof(boxSize), "Box size must be positive.");
        if (reversalAmount < 1) throw new ArgumentOutOfRangeException(nameof(reversalAmount), "Reversal amount must be >= 1.");

        int count = candles.Count;
        var zeroAlloc = ArrayPool<ZeroAllocCandleData>.Shared.Rent(count);
        try
        {
            for (int i = 0; i < count; i++)
            {
                var c = candles[i];
                zeroAlloc[i] = new ZeroAllocCandleData(c.Timestamp, c.Open, c.High, c.Low, c.Close, (long)c.Volume);
            }

            var parameters = new PointAndFigureParameters(boxSize, reversalAmount, roundingMode);
            var adapter = PointAndFigureCalculator.Calculate(zeroAlloc.AsMemory(0, count), parameters);

            int columnCount = adapter.Count;
            if (columnCount == 0) return;

            var times = adapter.Timestamps.Span;
            var opens = adapter.Opens.Span;
            var highs = adapter.Highs.Span;
            var lows = adapter.Lows.Span;
            var closes = adapter.Closes.Span;
            var vols = adapter.Volumes.Span;
            var ed = adapter.ElementDates.Span;

            if (outputBuffer.Capacity < columnCount) outputBuffer.Capacity = columnCount;
            for (int i = 0; i < columnCount; i++)
            {
                outputBuffer.Add(new CoreCandleData(times[i], opens[i], highs[i], lows[i], closes[i], vols[i]));
            }

            if (elementDatesBuffer != null && ed.Length == columnCount)
            {
                elementDatesBuffer.AddRange(ed);
            }
        }
        finally
        {
            ArrayPool<ZeroAllocCandleData>.Shared.Return(zeroAlloc, clearArray: true);
        }
    }
}
