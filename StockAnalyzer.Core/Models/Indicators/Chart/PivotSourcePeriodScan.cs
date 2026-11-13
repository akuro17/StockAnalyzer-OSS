using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models.Parameters;
using StockAnalyzer.Core.Services;

namespace StockAnalyzer.Core.Models.Indicators.Chart
{
    /// <summary>High, low and close of one completed period.</summary>
    internal readonly record struct PivotPeriodHlc(decimal High, decimal Low, decimal Close);

    /// <summary>
    /// The one scan that gives every bar the high, low and close of the last COMPLETED period before the bar's own period
    /// (shared by ClassicPivotPoints and PivotPointsAdvanced). Chronological order is assumed; a bar uses only bars before its own period.
    /// </summary>
    internal static class PivotSourcePeriodScan
    {
        /// <summary>
        /// Result[i] = H/L/C of the completed period before bar i's period, or null while no such period exists.
        /// <paramref name="period"/> must be <see cref="PivotSourcePeriod.Week"/> or <see cref="PivotSourcePeriod.Month"/>
        /// (PreviousBar is the callers' own per-bar path). Week = ISO week key, Month = calendar month key of <see cref="TimeFrameAggregator.GetPeriodKey"/>.
        /// </summary>
        internal static PivotPeriodHlc?[] ScanPreviousPeriods(IReadOnlyList<CoreCandleData> candles, PivotSourcePeriod period)
        {
            TimeFrame frame = period switch
            {
                PivotSourcePeriod.Week => TimeFrame.W1,
                PivotSourcePeriod.Month => TimeFrame.MN1,
                _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Only Week and Month are scanned."),
            };

            int count = candles.Count;
            var result = new PivotPeriodHlc?[count];
            if (count == 0)
            {
                return result;
            }

            PivotPeriodHlc? previous = null;
            decimal accHigh = candles[0].High;
            decimal accLow = candles[0].Low;
            decimal accClose = candles[0].Close;
            int previousKey = TimeFrameAggregator.GetPeriodKey(candles[0].Timestamp, frame);

            result[0] = null;
            for (int i = 1; i < count; i++)
            {
                CoreCandleData candle = candles[i];
                int key = TimeFrameAggregator.GetPeriodKey(candle.Timestamp, frame);
                if (key != previousKey)
                {
                    // The period that just ended becomes the source; the accumulator restarts with this bar.
                    previous = new PivotPeriodHlc(accHigh, accLow, accClose);
                    accHigh = candle.High;
                    accLow = candle.Low;
                    accClose = candle.Close;
                    previousKey = key;
                }
                else
                {
                    if (candle.High > accHigh) accHigh = candle.High;
                    if (candle.Low < accLow) accLow = candle.Low;
                    accClose = candle.Close;
                }

                result[i] = previous;
            }

            return result;
        }
    }
}
