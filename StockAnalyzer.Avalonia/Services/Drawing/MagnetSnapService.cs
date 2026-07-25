using System;
using System.Collections.Generic;
using Avalonia;
using StockAnalyzer.Avalonia.Drawing;
using StockAnalyzer.Core.Models;

namespace StockAnalyzer.Avalonia.Services.Drawing;

/// <summary>
/// Default implementation of <see cref="IMagnetSnapService"/>.
/// </summary>
public class MagnetSnapService : IMagnetSnapService
{
    // Magnet radius
    private const double MagnetRadius = 5.0;

    public (ChartPoint SnappedChartPoint, global::Avalonia.Point SnappedScreenPoint, bool IsSnapped) GetMagnetSnap(
        global::Avalonia.Point mouseScreenPoint,
        IReadOnlyList<CoreCandleData> candles,
        ICoordinateTransform coordinateTransform)
    {
        if (candles == null || candles.Count == 0 || coordinateTransform == null)
            return (coordinateTransform?.ScreenToChart(mouseScreenPoint) ?? new ChartPoint(DateTime.MinValue, 0), mouseScreenPoint, false);

        // Check if we are in Time Mode (Linear Time Axis)
        bool isTimeMode = false;
        if (coordinateTransform is GenericCoordinateTransform gct && gct.Mode == ChartAxisMode.Time)
        {
            isTimeMode = true;
        }
        else if (coordinateTransform is LinearCoordinateTransform) // Backwards compatibility for pure linear
        {
            isTimeMode = true;
        }

        if (isTimeMode)
        {
            // Binary Search Strategy for Time Axis (Optimized & Gap-Resistant)
            var chartPoint = coordinateTransform.ScreenToChart(mouseScreenPoint);
            // Ensure we clamp the search time to the range covered by candles to avoid issues with far-off mouse clicks
            var searchTime = chartPoint.Time;
            if (searchTime < candles[0].Timestamp) searchTime = candles[0].Timestamp;
            if (searchTime > candles[candles.Count - 1].Timestamp) searchTime = candles[candles.Count - 1].Timestamp;

            int idx = FindClosestIndex(candles, searchTime);

            if (idx >= 0 && idx < candles.Count)
            {
                var candle = candles[idx];
                
                // Calculate screen position of the candidate
                var candleCenterChartPoint = new ChartPoint(candle.Timestamp, candle.Close);
                var candleScreenPt = coordinateTransform.ChartToScreen(candleCenterChartPoint);

                // Check horizontal distance
                // We use a more generous threshold for "Magnet" feeling in Time Mode, 
                // allowing snapping across gaps (like weekends) if reasonable.
                // Dynamic threshold: Min 50px or 0.75 * CandleWidth (for zoom)
                double threshold = 50.0; 
                
                // Calculate approximate candle width for dynamic threshold
                if (candles.Count > 1 && idx < candles.Count - 1)
                {
                    var p1 = coordinateTransform.ChartToScreen(new ChartPoint(candles[idx].Timestamp, 0));
                    var p2 = coordinateTransform.ChartToScreen(new ChartPoint(candles[idx+1].Timestamp, 0));
                    double width = Math.Abs(p2.X - p1.X);
                    threshold = Math.Max(50.0, width * 0.75);
                }
                else if (candles.Count > 1 && idx > 0)
                {
                     var p1 = coordinateTransform.ChartToScreen(new ChartPoint(candles[idx-1].Timestamp, 0));
                     var p2 = coordinateTransform.ChartToScreen(new ChartPoint(candles[idx].Timestamp, 0));
                     double width = Math.Abs(p2.X - p1.X);
                     threshold = Math.Max(50.0, width * 0.75);
                }

                
                if (Math.Abs(candleScreenPt.X - mouseScreenPoint.X) <= threshold)
                {
                   // Find best OHLC match within this candle
                   var match = FindBestOHLCMatch(candle, mouseScreenPoint, coordinateTransform, MagnetRadius);
                   if (match.IsSnapped)
                   {
                       return match;
                   }
                }
            }
            
            // If binary search found nothing close, return unsnapped
            return (chartPoint, mouseScreenPoint, false);
        }

        // ---------------------------------------------------------
        // Fallback / Index Mode Logic (Iterative)
        // ---------------------------------------------------------

        global::Avalonia.Point bestMatchScreen = mouseScreenPoint;
        ChartPoint bestMatchChart = coordinateTransform.ScreenToChart(mouseScreenPoint);
        double minDistance = MagnetRadius;
        bool found = false;

        // Calculate dynamic threshold based on candle width (Interval) to handle Zoom In
        double dynamicThreshold = MagnetRadius;
        if (candles.Count > 1)
        {
             var p1 = coordinateTransform.ChartToScreen(new ChartPoint(candles[0].Timestamp, 0));
             var p2 = coordinateTransform.ChartToScreen(new ChartPoint(candles[1].Timestamp, 0));
             double intervalWidth = Math.Abs(p2.X - p1.X);
             // Ensure we snap if cursor is anywhere within the candle's time slot
             dynamicThreshold = Math.Max(MagnetRadius, intervalWidth / 2.0);
        }

        // Iterate visible candles to find nearest OHLC point
        foreach (var candle in candles)
        {
            // Optimization: First check horizontal distance (Pixel X)
            var candleCenterChartPoint = new ChartPoint(candle.Timestamp, candle.Close);
            var candleCenterX = coordinateTransform.ChartToScreen(candleCenterChartPoint).X;
            
            if (Math.Abs(candleCenterX - mouseScreenPoint.X) > dynamicThreshold)
                continue;

            // Check all OHLC prices
            var match = FindBestOHLCMatch(candle, mouseScreenPoint, coordinateTransform, minDistance);
            if (match.IsSnapped)
            {
                minDistance = Math.Sqrt(Math.Pow(match.SnappedScreenPoint.X - mouseScreenPoint.X, 2) + Math.Pow(match.SnappedScreenPoint.Y - mouseScreenPoint.Y, 2));
                bestMatchScreen = match.SnappedScreenPoint;
                bestMatchChart = match.SnappedChartPoint;
                found = true;
            }
        }

        return (bestMatchChart, bestMatchScreen, found);
    }

    private int FindClosestIndex(IReadOnlyList<CoreCandleData> candles, DateTime targetTime)
    {
        int min = 0;
        int max = candles.Count - 1;
        
        if (targetTime <= candles[min].Timestamp) return min;
        if (targetTime >= candles[max].Timestamp) return max;

        while (min <= max)
        {
            int mid = (min + max) / 2;
            DateTime midTime = candles[mid].Timestamp;

            if (midTime == targetTime) return mid;
            if (midTime < targetTime) min = mid + 1;
            else max = mid - 1;
        }

        if (min >= candles.Count) return candles.Count - 1;
        if (min < 0) return 0;

        if (min > 0)
        {
            DateTime t1 = candles[min - 1].Timestamp;
            DateTime t2 = candles[min].Timestamp;
            if ((targetTime - t1) < (t2 - targetTime)) return min - 1;
        }

        return min;
    }

    private (ChartPoint SnappedChartPoint, global::Avalonia.Point SnappedScreenPoint, bool IsSnapped) FindBestOHLCMatch(
        CoreCandleData candle, 
        global::Avalonia.Point mouseScreenPoint, 
        ICoordinateTransform coordinateTransform, 
        double maxDistance)
    {
        decimal[] prices = { candle.Open, candle.High, candle.Low, candle.Close };
        
        global::Avalonia.Point bestScreen = mouseScreenPoint;
        ChartPoint bestChart = new ChartPoint(candle.Timestamp, 0);
        bool anyFound = false;
        double minDist = maxDistance;

        foreach (var price in prices)
        {
            var cp = new ChartPoint(candle.Timestamp, price);
            var screenPt = coordinateTransform.ChartToScreen(cp);

            double dist = Math.Sqrt(
                Math.Pow(screenPt.X - mouseScreenPoint.X, 2) +
                Math.Pow(screenPt.Y - mouseScreenPoint.Y, 2));

            if (dist < minDist)
            {
                minDist = dist;
                bestScreen = screenPt;
                bestChart = cp;
                anyFound = true;
            }
        }
        
        return (bestChart, bestScreen, anyFound);
    }
}
