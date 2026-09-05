using StockAnalyzer.Core.MathUtils;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// Converts OHLCV candle data into Point &amp; Figure (P&amp;F) columns.
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
    /// <returns>List of CoreCandleData, each representing one P&F column.</returns>
    public static List<CoreCandleData> Convert(IReadOnlyList<CoreCandleData> candles, decimal boxSize, int reversalAmount = ChartConstants.DefaultReversalAmount, ChartRoundingMode roundingMode = ChartRoundingMode.None)
    {
        var columns = new List<CoreCandleData>();
        Convert(candles, boxSize, reversalAmount, columns, roundingMode);
        return columns;
    }

    public static void Convert(IReadOnlyList<CoreCandleData> candles, decimal boxSize, int reversalAmount, List<CoreCandleData> outputBuffer, ChartRoundingMode roundingMode = ChartRoundingMode.None)
    {
        outputBuffer.Clear();
        if (candles == null) return;
        if (boxSize <= 0) throw new ArgumentOutOfRangeException(nameof(boxSize), "Box size must be positive.");
        if (reversalAmount < 1) throw new ArgumentOutOfRangeException(nameof(reversalAmount), "Reversal amount must be >= 1.");

        decimal reversalValue = boxSize * reversalAmount;

        if (candles.Count == 0) return;

        // Initialize baseline from first candle's close, quantized to box boundary
        var first = candles[0];
        decimal baselinePrice = QuantizeDown(first.Close, boxSize, roundingMode);

        // State: 0 = waiting for first directional move, 1 = Up column, -1 = Down column
        int state = 0;

        // Current column boundaries (quantized)
        decimal colTop = baselinePrice;
        decimal colBottom = baselinePrice;
        DateTime colTimestamp = first.Timestamp;

        for (int i = 1; i < candles.Count; i++)
        {
            var c = candles[i];
            decimal high = c.High;
            decimal low = c.Low;

            if (state == 0)
            {
                // Waiting for first directional move
                if (high >= baselinePrice + boxSize)
                {
                    // First move is Up
                    state = 1;
                    colTop = QuantizeDown(high, boxSize, roundingMode);
                    colBottom = baselinePrice;
                    colTimestamp = c.Timestamp;
                    outputBuffer.Add(MakeUpColumn(colTimestamp, colBottom, colTop));
                }
                else if (low <= baselinePrice - boxSize)
                {
                    // First move is Down
                    state = -1;
                    colTop = baselinePrice;
                    colBottom = QuantizeUp(low, boxSize, roundingMode);
                    colTimestamp = c.Timestamp;
                    outputBuffer.Add(MakeDownColumn(colTimestamp, colTop, colBottom));
                }
                continue;
            }

            if (state == 1) // Current: Up column (X)
            {
                // 1. Check extension (High pushes column higher)
                decimal quantizedHigh = QuantizeDown(high, boxSize, roundingMode);
                if (quantizedHigh > colTop)
                {
                    // Extend up column
                    colTop = quantizedHigh;
                    colTimestamp = c.Timestamp;
                    outputBuffer[outputBuffer.Count - 1] = MakeUpColumn(colTimestamp, colBottom, colTop);
                }
                // 2. Check reversal to Down (only if no extension)
                else if (low <= colTop - reversalValue)
                {
                    // Reverse: new Down column starts 1 box below previous top
                    decimal newTop = colTop - boxSize;
                    decimal newBottom = QuantizeUp(low, boxSize, roundingMode);
                    state = -1;
                    colTop = newTop;
                    colBottom = newBottom;
                    colTimestamp = c.Timestamp;
                    outputBuffer.Add(MakeDownColumn(colTimestamp, colTop, colBottom));
                }
            }
            else // state == -1, Current: Down column (O)
            {
                // 1. Check extension (Low pushes column lower)
                decimal quantizedLow = QuantizeUp(low, boxSize, roundingMode);
                if (quantizedLow < colBottom)
                {
                    // Extend down column
                    colBottom = quantizedLow;
                    colTimestamp = c.Timestamp;
                    outputBuffer[outputBuffer.Count - 1] = MakeDownColumn(colTimestamp, colTop, colBottom);
                }
                // 2. Check reversal to Up (only if no extension)
                else if (high >= colBottom + reversalValue)
                {
                    // Reverse: new Up column starts 1 box above previous bottom
                    decimal newBottom = colBottom + boxSize;
                    decimal newTop = QuantizeDown(high, boxSize, roundingMode);
                    state = 1;
                    colBottom = newBottom;
                    colTop = newTop;
                    colTimestamp = c.Timestamp;
                    outputBuffer.Add(MakeUpColumn(colTimestamp, colBottom, colTop));
                }
            }
        }

    }

    /// <summary>
    /// Creates an Up column (X marks) CoreCandleData.
    /// Open = bottom, Close = top → IsBullish = true
    /// </summary>
    private static CoreCandleData MakeUpColumn(DateTime timestamp, decimal bottom, decimal top)
    {
        return new CoreCandleData(
            timestamp,
            bottom,  // Open  = column bottom
            top,     // High  = column top
            bottom,  // Low   = column bottom
            top,     // Close = column top
            0
        );
    }

    /// <summary>
    /// Creates a Down column (O marks) CoreCandleData.
    /// Open = top, Close = bottom → IsBullish = false
    /// </summary>
    private static CoreCandleData MakeDownColumn(DateTime timestamp, decimal top, decimal bottom)
    {
        return new CoreCandleData(
            timestamp,
            top,     // Open  = column top
            top,     // High  = column top
            bottom,  // Low   = column bottom
            bottom,  // Close = column bottom
            0
        );
    }

    /// <summary>
    /// Quantize price downward to the nearest box boundary.
    /// Example: QuantizeDown(107, 5) = 105
    /// </summary>
    private static decimal QuantizeDown(decimal price, decimal boxSize, ChartRoundingMode mode)
    {
        if (mode == ChartRoundingMode.None)
        {
            return Math.Floor(price / boxSize) * boxSize;
        }
        return ChartMath.Quantize(price, boxSize, mode);
    }

    /// <summary>
    /// Quantize price upward to the nearest box boundary.
    /// Example: QuantizeUp(93, 5) = 95
    /// </summary>
    private static decimal QuantizeUp(decimal price, decimal boxSize, ChartRoundingMode mode)
    {
        if (mode == ChartRoundingMode.None)
        {
            return Math.Ceiling(price / boxSize) * boxSize;
        }
        return ChartMath.Quantize(price, boxSize, mode);
    }
}
