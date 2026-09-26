using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// Converts time-series CandleData into Three Line Break blocks.
/// </summary>
public static class ThreeLineBreakConverter
{
    public static ImmutableArray<ThreeLineBreakBlock> Convert(IReadOnlyList<CoreCandleData> candles, int lineCount = 3, decimal tolerance = ChartConstants.ThreeLineBreakTrendThreshold, decimal minimumMove = 0m)
    {
        var builder = ImmutableArray.CreateBuilder<ThreeLineBreakBlock>();
        ConvertInto(candles, builder, lineCount, tolerance, minimumMove);
        return builder.ToImmutable();
    }

    public static void Convert(IReadOnlyList<CoreCandleData> candles, int lineCount, decimal tolerance, decimal minimumMove, List<ThreeLineBreakBlock> outputBuffer)
    {
        ConvertInto(candles, outputBuffer, lineCount, tolerance, minimumMove);
    }

    /// <summary>
    /// Core conversion logic using buffer injection to prevent allocations.
    /// </summary>
    private static void ConvertInto(IReadOnlyList<CoreCandleData> candles, IList<ThreeLineBreakBlock> output, int lineCount, decimal tolerance, decimal minimumMove)
    {
        output.Clear();
        if (candles == null || candles.Count < 2) return;

        var strategy = new StandardThreeLineBreakStrategy();
        
        if (!strategy.TryDetermineInitialTrend(candles, out int startIndex, out bool isUp))
        {
            return;
        }

        output.Add(new ThreeLineBreakBlock(
            StartDate: candles[0].Timestamp,
            EndDate: candles[startIndex].Timestamp,
            OpenPrice: candles[0].Close,
            ClosePrice: candles[startIndex].Close,
            IsUp: isUp
        ));

        for (int i = startIndex + 1; i < candles.Count; i++)
        {
            var candle = candles[i];
            var currentClose = candle.Close;
            var lastBlock = output[output.Count - 1];

            // --- MINIMUM MOVE FILTER ---
            if (minimumMove > 0m && Math.Abs(currentClose - lastBlock.ClosePrice) < minimumMove)
            {
                continue; // Ignore this candle (Noise filtering)
            }

            bool currentTrendIsUp = lastBlock.IsUp;

            if (strategy.ShouldContinue(currentClose, lastBlock, tolerance))
            {
                output.Add(new ThreeLineBreakBlock(
                    StartDate: lastBlock.EndDate,
                    EndDate: candle.Timestamp,
                    OpenPrice: lastBlock.ClosePrice,
                    ClosePrice: currentClose,
                    IsUp: currentTrendIsUp
                ));
            }
            else if (strategy.ShouldReverse(currentClose, output, currentTrendIsUp, lineCount, tolerance, out _))
            {
                output.Add(new ThreeLineBreakBlock(
                    StartDate: lastBlock.EndDate,
                    EndDate: candle.Timestamp,
                    OpenPrice: lastBlock.ClosePrice,
                    ClosePrice: currentClose,
                    IsUp: !currentTrendIsUp
                ));
            }
        }
    }

    /// <summary>
    /// Converts Three Line Break blocks to CoreCandleData for rendering compatibility.
    /// Supports buffer injection for intermediate blocks to achieve true Zero-Allocation.
    /// </summary>
    public static void ConvertToCoreCandleData(
        IReadOnlyList<CoreCandleData> candles, 
        List<CoreCandleData> outputBuffer, 
        List<ThreeLineBreakBlock>? tempBlockBuffer = null,
        int lineCount = 3, 
        decimal minimumMove = 0m)
    {
        // Use injected buffer if available, otherwise fallback to local (minor allocation)
        var blocks = tempBlockBuffer ?? new List<ThreeLineBreakBlock>(candles.Count / 2);
        ConvertInto(candles, blocks, lineCount, ChartConstants.ThreeLineBreakTrendThreshold, minimumMove);
        
        outputBuffer.Clear();
        if (outputBuffer.Capacity < blocks.Count) outputBuffer.Capacity = blocks.Count;

        for (int i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i];
            outputBuffer.Add(new CoreCandleData(
                b.EndDate,
                b.OpenPrice,
                b.High,
                b.Low,
                b.ClosePrice,
                0
            ));
        }
    }

    /// <summary>
    /// Legacy overload for backward compatibility.
    /// </summary>
    public static ImmutableArray<CoreCandleData> ConvertToCoreCandleData(IReadOnlyList<CoreCandleData> candles, int lineCount = 3, decimal minimumMove = 0m)
    {
        var output = new List<CoreCandleData>();
        ConvertToCoreCandleData(candles, output, null, lineCount, minimumMove);
        return output.ToImmutableArray();
    }
}
