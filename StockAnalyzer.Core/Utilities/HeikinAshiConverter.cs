using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utilities;

/// <summary>
/// Heikin-Ashi converter using Core models.
/// </summary>
public static class HeikinAshiConverter
{
    /// <summary>
    /// Converts standard CandleData to Heikin-Ashi CandleData.
    /// </summary>
    public static List<CoreCandleData> Convert(IReadOnlyList<CoreCandleData>? originalCandles)
    {
        var result = new List<CoreCandleData>(originalCandles?.Count ?? 0);
        Convert(originalCandles, result);
        return result;
    }

    public static void Convert(IReadOnlyList<CoreCandleData>? originalCandles, List<CoreCandleData> outputBuffer)
    {
        outputBuffer.Clear();
        if (originalCandles == null || originalCandles.Count == 0)
            return;

        if (outputBuffer.Capacity < originalCandles.Count)
            outputBuffer.Capacity = originalCandles.Count;

        decimal prevHaOpen = 0m;
        decimal prevHaClose = 0m;
        bool isFirstCandle = true;

        for (int i = 0; i < originalCandles.Count; i++)
        {
            var candle = originalCandles[i];
            // CoreCandleData does not have IsValid() currently, but we can check if it's empty if needed.
            // For now, assume all candles in IReadOnlyList are valid as per project patterns.

            if (isFirstCandle)
            {
                var (initHaOpen, initHaClose) = CalculateInitialValues(candle);
                prevHaOpen = initHaOpen;
                prevHaClose = initHaClose;
                isFirstCandle = false;

                decimal initHaHigh = Math.Max(candle.High, Math.Max(initHaOpen, initHaClose));
                decimal initHaLow = Math.Min(candle.Low, Math.Min(initHaOpen, initHaClose));

                outputBuffer.Add(new CoreCandleData(
                    candle.Timestamp,
                    initHaOpen,
                    initHaHigh,
                    initHaLow,
                    initHaClose,
                    candle.Volume
                ));

                continue;
            }

            var haCandle = ConvertSingle(candle, prevHaOpen, prevHaClose);
            outputBuffer.Add(haCandle);

            prevHaOpen = haCandle.Open;
            prevHaClose = haCandle.Close;
        }
    }

    public static CoreCandleData ConvertSingle(CoreCandleData candle, decimal prevHaOpen, decimal prevHaClose)
    {
        decimal haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4m;
        decimal haOpen = (prevHaOpen + prevHaClose) / 2m;
        decimal haHigh = Math.Max(candle.High, Math.Max(haOpen, haClose));
        decimal haLow = Math.Min(candle.Low, Math.Min(haOpen, haClose));

        return new CoreCandleData(
            candle.Timestamp,
            haOpen,
            haHigh,
            haLow,
            haClose,
            candle.Volume
        );
    }

    public static (decimal haOpen, decimal haClose) CalculateInitialValues(CoreCandleData firstCandle)
    {
        decimal haOpen = (firstCandle.Open + firstCandle.Close) / 2m;
        decimal haClose = (firstCandle.Open + firstCandle.High + firstCandle.Low + firstCandle.Close) / 4m;

        return (haOpen, haClose);
    }
}
