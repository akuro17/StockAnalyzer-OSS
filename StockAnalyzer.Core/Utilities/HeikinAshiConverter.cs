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
        var (haOpen, haClose) = NextState(candle, prevHaOpen, prevHaClose);
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

    /// <summary>
    /// One step of the recursion of indicators_formulas_0466: the Heikin-Ashi (Open, Close) of <paramref name="candle"/> from the
    /// (Open, Close) of the previous bar. Allocation-free (a value tuple), so it is safe in render loops and screening;
    /// <see cref="ConvertSingle"/>, <see cref="GetStateAt(IReadOnlyList{CoreCandleData}, int, int)"/> and the price helpers share it.
    /// </summary>
    public static (decimal haOpen, decimal haClose) NextState(CoreCandleData candle, decimal prevHaOpen, decimal prevHaClose)
    {
        decimal haClose = (candle.Open + candle.High + candle.Low + candle.Close) / 4m;
        decimal haOpen = (prevHaOpen + prevHaClose) / 2m;
        return (haOpen, haClose);
    }

    /// <summary>
    /// Heikin-Ashi (Open, Close) of bar <paramref name="index"/> by the specified recursion
    /// (indicators_formulas_0466: HA_Open_t = (HA_Open_{t-1} + HA_Close_{t-1}) / 2), treating bar <paramref name="seedIndex"/>
    /// as the seed bar (<see cref="CalculateInitialValues"/>). <c>seedIndex = 0</c> gives the exact series value; a later seed
    /// is a warm-up whose error halves every bar. Reads only bars up to <paramref name="index"/>; does not allocate.
    /// </summary>
    public static (decimal haOpen, decimal haClose) GetStateAt(IReadOnlyList<CoreCandleData> candles, int index, int seedIndex = 0)
        => GetStateAt(candles, index, seedIndex, out _);

    /// <summary>
    /// Same as <see cref="GetStateAt(IReadOnlyList{CoreCandleData}, int, int)"/> and additionally returns the state of the bar before
    /// <paramref name="index"/> from the same pass (<paramref name="previousState"/> is null when <paramref name="index"/> equals
    /// <paramref name="seedIndex"/>, i.e. the seed bar has no predecessor in the warm-up).
    /// </summary>
    public static (decimal haOpen, decimal haClose) GetStateAt(IReadOnlyList<CoreCandleData> candles, int index, int seedIndex,
        out (decimal haOpen, decimal haClose)? previousState)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (seedIndex < 0 || seedIndex > index)
            throw new ArgumentOutOfRangeException(nameof(seedIndex), $"Require 0 <= seedIndex ({seedIndex}) <= index ({index}).");
        if (index >= candles.Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Require index ({index}) < Count ({candles.Count}).");

        var state = CalculateInitialValues(candles[seedIndex]);
        (decimal haOpen, decimal haClose)? previous = null;
        for (int i = seedIndex + 1; i <= index; i++)
        {
            previous = state;
            state = NextState(candles[i], state.haOpen, state.haClose);
        }

        previousState = previous;
        return state;
    }

    public static (decimal haOpen, decimal haClose) CalculateInitialValues(CoreCandleData firstCandle)
    {
        decimal haOpen = (firstCandle.Open + firstCandle.Close) / 2m;
        decimal haClose = (firstCandle.Open + firstCandle.High + firstCandle.Low + firstCandle.Close) / 4m;

        return (haOpen, haClose);
    }
}
