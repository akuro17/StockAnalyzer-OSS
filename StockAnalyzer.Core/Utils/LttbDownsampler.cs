using System;
using System.Collections.Generic;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Core.Utils;

/// <summary>
/// Largest-Triangle-Three-Buckets (LTTB) downsampling algorithm implementation.
/// Preserves the visual characteristics (highs/lows) of a time series while reducing the number of points.
/// </summary>
public static class LttbDownsampler
{
    /// <summary>
    /// Downsamples a list of CandleData using the LTTB algorithm.
    /// Returns the number of points to draw. The indices of the selected points are written to <paramref name="destination"/>.
    /// </summary>
    /// <param name="data">The original data points.</param>
    /// <param name="threshold">The maximum number of points to keep.</param>
    /// <param name="destination">An array or span to store the selected indices. Must be at least <paramref name="threshold"/> length.</param>
    /// <returns>The number of indices written to the destination.</returns>
    public static int Downsample(IReadOnlyList<CoreCandleData> data, int threshold, Span<int> destination)
    {
        if (data == null || data.Count == 0 || threshold <= 0) return 0;
        int dataLength = data.Count;
        if (threshold >= dataLength)
        {
            for (int i = 0; i < dataLength; i++)
            {
                destination[i] = i;
            }
            return dataLength;
        }

        if (threshold <= 2)
        {
            destination[0] = 0;
            if (threshold == 2)
            {
                destination[1] = dataLength - 1;
            }
            return threshold;
        }

        destination[0] = 0; // Always add the first point

        double every = (double)(dataLength - 2) / (threshold - 2);
        int a = 0; // Current selected point index

        for (int i = 0; i < threshold - 2; i++)
        {
            // Calculate next bucket's average point
            int nextBucketStart = (int)Math.Floor((i + 1) * every) + 1;
            int nextBucketEnd = (int)Math.Floor((i + 2) * every) + 1;
            if (nextBucketEnd > dataLength) nextBucketEnd = dataLength;
            
            int nextBucketSize = nextBucketEnd - nextBucketStart;
            double avgX = 0, avgY = 0;
            if (nextBucketSize > 0)
            {
                for (int j = nextBucketStart; j < nextBucketEnd; j++)
                {
                    avgX += j;
                    avgY += (double)data[j].Close;
                }
                avgX /= nextBucketSize;
                avgY /= nextBucketSize;
            }
            else
            {
                avgX = nextBucketStart;
                avgY = (double)data[a].Close; // Fallback
            }

            // Find point in current bucket containing max area
            int currBucketStart = (int)Math.Floor(i * every) + 1;
            int currBucketEnd = (int)Math.Floor((i + 1) * every) + 1;
            if (currBucketEnd > dataLength) currBucketEnd = dataLength;

            double maxArea = -1;
            int maxAreaIndex = currBucketStart;

            double pointAx = a;
            double pointAy = (double)data[a].Close;

            for (int j = currBucketStart; j < currBucketEnd; j++)
            {
                double pointBx = j;
                double pointBy = (double)data[j].Close;
                
                double area = Math.Abs(
                    (pointAx - avgX) * (pointBy - pointAy) -
                    (pointAx - pointBx) * (avgY - pointAy)
                ) * 0.5;

                if (area > maxArea)
                {
                    maxArea = area;
                    maxAreaIndex = j;
                }
            }

            destination[i + 1] = maxAreaIndex;
            a = maxAreaIndex;
        }

        destination[threshold - 1] = dataLength - 1; // Always add the last point
        return threshold;
    }

    /// <summary>
    /// Downsamples a series of decimal values using the LTTB algorithm.
    /// Returns the number of points to draw. The indices of the selected points are written to <paramref name="destination"/>.
    /// </summary>
    public static int Downsample(ReadOnlySpan<decimal?> data, int threshold, Span<int> destination)
    {
        if (data.IsEmpty || threshold <= 0) return 0;
        int dataLength = data.Length;
        if (threshold >= dataLength)
        {
            for (int i = 0; i < dataLength; i++)
            {
                destination[i] = i;
            }
            return dataLength;
        }

        if (threshold <= 2)
        {
            destination[0] = 0;
            if (threshold == 2)
            {
                destination[1] = dataLength - 1;
            }
            return threshold;
        }

        destination[0] = 0;

        double every = (double)(dataLength - 2) / (threshold - 2);
        int a = 0;

        for (int i = 0; i < threshold - 2; i++)
        {
            var valA = data[a];
            int nextBucketStart = (int)Math.Floor((i + 1) * every) + 1;
            int nextBucketEnd = (int)Math.Floor((i + 2) * every) + 1;
            if (nextBucketEnd > dataLength) nextBucketEnd = dataLength;
            
            int nextBucketSize = nextBucketEnd - nextBucketStart;
            double avgX = 0, avgY = 0;
            if (nextBucketSize > 0)
            {
                int count = 0;
                for (int j = nextBucketStart; j < nextBucketEnd; j++)
                {
                    var val = data[j];
                    if (val.HasValue)
                    {
                        avgX += j;
                        avgY += (double)val.Value;
                        count++;
                    }
                }
                if (count > 0)
                {
                    avgX /= count;
                    avgY /= count;
                }
                else
                {
                    avgX = nextBucketStart;
                    avgY = valA.HasValue ? (double)valA.Value : 0;
                }
            }
            else
            {
                avgX = nextBucketStart;
                avgY = valA.HasValue ? (double)valA.Value : 0;
            }

            int currBucketStart = (int)Math.Floor(i * every) + 1;
            int currBucketEnd = (int)Math.Floor((i + 1) * every) + 1;
            if (currBucketEnd > dataLength) currBucketEnd = dataLength;

            double maxArea = -1;
            int maxAreaIndex = currBucketStart;

            double pointAx = a;
            double pointAy = valA.HasValue ? (double)valA.Value : 0;

            for (int j = currBucketStart; j < currBucketEnd; j++)
            {
                var valJ = data[j];
                if (!valJ.HasValue) continue;

                double pointBx = j;
                double pointBy = (double)valJ.Value;
                
                double area = Math.Abs(
                    (pointAx - avgX) * (pointBy - pointAy) -
                    (pointAx - pointBx) * (avgY - pointAy)
                ) * 0.5;

                if (area > maxArea)
                {
                    maxArea = area;
                    maxAreaIndex = j;
                }
            }

            destination[i + 1] = maxAreaIndex;
            a = maxAreaIndex;
        }

        destination[threshold - 1] = dataLength - 1;
        return threshold;
    }
}
